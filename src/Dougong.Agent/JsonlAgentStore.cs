using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// append-only JSONL 会话存储。写入后强制 flush，返回即代表已提交。
/// </summary>
/// <remarks>
/// 第一条记录是会话头（含 <see cref="StoreRecordKind"/> 之外的元数据），
/// 用于声明持久化级别与单写者租约信息。
/// <para>
/// 已知边界：这里用 <see cref="FileStream.FlushAsync(CancellationToken)"/> 加上
/// <c>FlushToDisk</c>，在 Windows 上把缓冲交给 OS，**不保证断电安全**。
/// 因此 <see cref="Durability"/> 声明为 <see cref="StoreDurability.SurvivesProcessCrash"/>，
/// 不声明 <see cref="StoreDurability.SurvivesPowerLoss"/>。
/// </para>
/// </remarks>
public sealed class JsonlAgentStore : IAgentStore
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _sequence;
    private bool _disposed;

    private JsonlAgentStore(string sessionId, FileStream stream, long lastSequence)
    {
        SessionId = sessionId;
        _stream = stream;
        _sequence = lastSequence;
    }

    public string SessionId { get; }

    public StoreDurability Durability => StoreDurability.SurvivesProcessCrash;

    /// <summary>
    /// 打开（或创建）会话文件并取得单写者租约。
    /// 已有文件被其他进程独占时抛 <see cref="IOException"/>，调用方应拒绝写入而不是重试覆盖。
    /// </summary>
    public static async Task<JsonlAgentStore> OpenAsync(
        string path, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // FileShare.None 即单写者租约：第二个进程会立刻失败，而不是交错写入。
        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 4096, useAsync: true);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"会话文件被占用，无法取得写租约：{path}", ex);
        }

        long lastSequence;
        try
        {
            lastSequence = await ScanForLastSequenceAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            stream.Seek(0, SeekOrigin.End);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new JsonlAgentStore(sessionId, stream, lastSequence);
    }

    public async ValueTask CommitAsync(
        StoreRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 序号由存储分配，调用方传入的 Sequence 被忽略：单写者保证递增。
            var next = _sequence + 1;
            var line = JsonSerializer.Serialize(new PersistedRecord(
                next, record.Kind.ToString(), record.Payload));

            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            try
            {
                await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                // 把 OS 缓冲刷到设备。仍不保证断电安全，见类型注释。
                _stream.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                // 提交失败的写入可能留下半条记录；截断回上一个已提交位置，
                // 避免把残缺数据留成"看起来已提交"的样子。
                TryTruncateToCommitted();
                throw new StoreCommitException("write", ex.Message, ex);
            }

            _sequence = next;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<StoreRecord> ReadCommittedAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 复用同一个写入流读取：单写者租约持有 FileShare.None，
        // 另开 FileStream 会因共享冲突失败。这里在写锁内把位置复位后顺序读。
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            var previous = _stream.Position;
            _stream.Seek(0, SeekOrigin.Begin);

            try
            {
                using var reader = new StreamReader(
                    _stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096, leaveOpen: true);

                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    PersistedRecord? parsed;
                    try
                    {
                        parsed = JsonSerializer.Deserialize<PersistedRecord>(line);
                    }
                    catch (JsonException)
                    {
                        // 尾部半条记录：停止读取，已提交部分仍然有效。
                        yield break;
                    }

                    if (parsed is null) yield break;
                    yield return new StoreRecord(
                        parsed.Sequence,
                        Enum.Parse<StoreRecordKind>(parsed.Kind),
                        parsed.Payload);
                }
            }
            finally
            {
                _stream.Seek(previous, SeekOrigin.Begin);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void TryTruncateToCommitted()
    {
        try
        {
            // 回退到最后一条完整记录的末尾。用换行回扫，避免依赖字节计数。
            var position = _stream.Position;
            _stream.Seek(0, SeekOrigin.Begin);
            var buffer = new byte[position];
            var read = 0;
            while (read < position)
            {
                var chunk = _stream.Read(buffer, read, (int)position - read);
                if (chunk <= 0) break;
                read += chunk;
            }

            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(0, read - 1));
            var committedLength = lastNewline + 1;
            _stream.SetLength(committedLength);
            _stream.Seek(committedLength, SeekOrigin.Begin);
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            // 截断本身失败时不掩盖原始提交错误。
        }
    }

    private static async Task<long> ScanForLastSequenceAsync(
        FileStream stream, CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);

        long last = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var parsed = JsonSerializer.Deserialize<PersistedRecord>(line);
                if (parsed is not null) last = parsed.Sequence;
            }
            catch (JsonException)
            {
                // 尾部半条记录：忽略，序号停在最后一条完整记录。
                break;
            }
        }

        return last;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _stream.Flush(flushToDisk: true);
        }
        catch (IOException) { }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private sealed record PersistedRecord(long Sequence, string Kind, JsonElement Payload);
}
