using System.Text;
using System.Text.Json;

namespace Dougong.Agent;

/// <summary>
/// 会话即树：一次分支 = 从某个已提交点复制出新文件。
/// </summary>
/// <remarks>
/// 为什么不能简单复制整份文件：分支点的意义是"从此处重来"，
/// 分叉点之后的记录必须被丢掉，否则新会话会带着旧决定继续跑。
/// <para>
/// 分支不修改源会话文件一个字节：源会话可能仍被另一个进程持写租约，
/// 而且"分支"不该让原会话变得不可解释。
/// </para>
/// </remarks>
public static class SessionBranch
{
    /// <summary>
    /// 从 <paramref name="sourcePath"/> 的 <paramref name="atSequence"/> 处复制出新会话。
    /// </summary>
    /// <param name="atSequence">
    /// 保留到哪一条（含）。传 null 表示保留全部已提交记录，只换一个新身份。
    /// </param>
    /// <returns>新会话文件的路径与它的会话标识。</returns>
    /// <exception cref="InvalidOperationException">
    /// 指定序号不存在，或该点之后还有记录但调用方意图不明（例如序号落在半条记录里）。
    /// </exception>
    public static async Task<BranchResult> CreateAsync(
        string sourcePath,
        string targetPath,
        string newSessionId,
        long? atSequence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSessionId);
        if (string.Equals(
                Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("分支目标不能与源会话相同。");

        // 源会话以只读方式打开：分支不该要求源会话先被关闭或接管写租约。
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: true);

        var kept = new List<string>();
        long lastSequence = 0;
        var reachedEnd = false;

        // 分支点不能停在工具交互中间：把 assistant 的 tool_calls 留下、
        // 却把对应的 tool 结果丢掉，会得到一条悬空调用，provider 会直接拒掉续跑
        // （实测 400 "must be followed by tool messages"）。
        // 处理方式是**向后补齐**到配对完成的位置，而不是回退：
        // 用户选的分叉点代表"保留到这里为止的上下文"，回退会悄悄少给一段。
        var pendingCalls = new HashSet<string>(StringComparer.Ordinal);

        using var reader = new StreamReader(
            source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            long sequence;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("Sequence", out var seq)
                    || seq.ValueKind is not JsonValueKind.Number)
                    break;
                sequence = seq.GetInt64();

                TrackPendingCalls(root, pendingCalls);
            }
            catch (JsonException)
            {
                // 尾部半条记录：到此为止，不影响已提交部分。
                break;
            }

            kept.Add(line);
            lastSequence = sequence;

            if (atSequence is { } limit && sequence >= limit)
            {
                reachedEnd = true;

                // 补齐：继续往后读，直到所有已声明的调用都有结果。
                // 补齐只为了消除悬空，不会越过下一个"新的用户输入"——
                // 那已经属于后续对话，不该被并进来。
                while (pendingCalls.Count > 0
                    && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } extra)
                {
                    if (string.IsNullOrWhiteSpace(extra)) continue;

                    try
                    {
                        using var document = JsonDocument.Parse(extra);
                        var root = document.RootElement;
                        if (!root.TryGetProperty("Sequence", out var seq)
                            || seq.ValueKind is not JsonValueKind.Number)
                            break;

                        var kind = root.TryGetProperty("Kind", out var kindElement)
                            ? kindElement.GetString() : null;
                        if (kind is nameof(StoreRecordKind.UserInput))
                        {
                            throw new InvalidOperationException(
                                "分支点处的工具调用在下一个用户输入之前没有结果；"
                                + "该分支会留下悬空的 tool_calls，provider 无法接受。");
                        }

                        TrackPendingCalls(root, pendingCalls);
                        kept.Add(extra);
                        lastSequence = seq.GetInt64();
                    }
                    catch (JsonException)
                    {
                        break;
                    }
                }

                break;
            }
        }

        if (atSequence is { } requested && !reachedEnd && lastSequence < requested)
            throw new InvalidOperationException(
                $"分支点 #{requested} 不存在；源会话最后一条已提交记录是 #{lastSequence}。");

        if (pendingCalls.Count > 0)
            throw new InvalidOperationException(
                "会话末尾存在没有结果的工具调用；该分支无法续跑，请先对账。");

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // FileShare.None：新会话立刻取得自己的写租约，避免两个进程写同一个分支。
        await using (var target = new FileStream(
            targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true))
        {
            foreach (var line in kept)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await target.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Flush(flushToDisk: true);
        }

        return new BranchResult(targetPath, newSessionId, lastSequence, kept.Count);
    }

    /// <summary>
    /// 跟踪工具交互配对：ModelResponse 里声明的调用记为待完成，
    /// 对应的 ToolCallCompleted 把它销账。集合非空即表示"有调用无结果"。
    /// </summary>
    private static void TrackPendingCalls(JsonElement record, HashSet<string> pending)
    {
        if (!record.TryGetProperty("Kind", out var kindElement)) return;
        if (!record.TryGetProperty("Payload", out var payload)) return;

        var kind = kindElement.GetString();
        if (kind is nameof(StoreRecordKind.ModelResponse))
        {
            if (payload.ValueKind is not JsonValueKind.Object) return;
            if (!payload.TryGetProperty("calls", out var calls)
                || calls.ValueKind is not JsonValueKind.Array) return;

            foreach (var call in calls.EnumerateArray())
            {
                if (call.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                    pending.Add(value);
            }
        }
        else if (kind is nameof(StoreRecordKind.ToolCallCompleted))
        {
            if (payload.ValueKind is not JsonValueKind.Object) return;
            if (payload.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                pending.Remove(value);
        }
    }

    /// <summary>
    /// 列出目录下的会话，按最后写入时间倒序。
    /// </summary>
    /// <remarks>
    /// 这里只报文件与基本统计，不解析完整历史：列表要为"选一个会话"服务，
    /// 解析全部记录会让列一个大目录变得很慢，而且没必要。
    /// 每条会话的摘要仍以打开后的重放结果为准。
    /// </remarks>
    public static IReadOnlyList<SessionSummary> List(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory)) return [];

        var results = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists) continue;
            }
            catch (IOException)
            {
                continue;
            }

            var (firstSequence, lastSequence, records, hasGap) = Scan(file);
            results.Add(new SessionSummary(
                file,
                Path.GetFileNameWithoutExtension(file),
                info.LastWriteTimeUtc,
                info.Length,
                records,
                firstSequence,
                lastSequence,
                hasGap));
        }

        return [.. results.OrderByDescending(r => r.LastWriteUtc)];
    }

    private static (long First, long Last, int Records, bool HasGap) Scan(string path)
    {
        long first = 0, last = 0, expected = 1;
        var records = 0;
        var gap = false;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (!document.RootElement.TryGetProperty("Sequence", out var seq)
                        || seq.ValueKind is not JsonValueKind.Number)
                        break;

                    var sequence = seq.GetInt64();
                    if (records == 0) first = sequence;
                    if (sequence != expected) gap = true;
                    expected = sequence + 1;
                    last = sequence;
                    records++;
                }
                catch (JsonException)
                {
                    // 尾部半条记录：停止扫描，已提交部分仍然有效。
                    break;
                }
            }
        }
        catch (IOException)
        {
            return (0, 0, 0, true);
        }

        return (first, last, records, gap);
    }
}

/// <summary>一次分支的结果。</summary>
public sealed record BranchResult(
    string Path,
    string SessionId,
    long LastSequence,
    int RecordCount);

/// <summary>会话列表中的一项。只描述文件与序号范围，不含消息内容。</summary>
public sealed record SessionSummary(
    string Path,
    string SessionId,
    DateTime LastWriteUtc,
    long SizeBytes,
    int RecordCount,
    long FirstSequence,
    long LastSequence,
    bool HasSequenceGap);
