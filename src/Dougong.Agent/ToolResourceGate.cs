namespace Dougong.Agent;

/// <summary>
/// 首版的资源协调：工具串行 + 工作区独占。
/// </summary>
/// <remarks>
/// 设计依据（docs/architecture.md §4.4）：
/// <list type="bullet">
/// <item>未声明或无法验证并发能力的工具默认 <see cref="ToolConcurrency.Serial"/></item>
/// <item>shell、未知资源、改变进程/环境状态的工具取工作区独占</item>
/// <item>首版只实现这两级；资源级读写键与跨工作区协调属后续扩展，
/// 不预先冻结键格式</item>
/// </list>
/// <para>
/// 与审批的顺序也很重要：**等待审批期间不持锁**。调用方必须先取审批、
/// 再进锁执行，否则一个慢审批会阻塞整个工作区。
/// </para>
/// </remarks>
public sealed class ToolResourceGate : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> _workspaceLocks = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// 取得执行权。<see cref="ToolConcurrency.Serial"/> 先取全局串行权、再取所属工作区的执行权；
    /// <see cref="ToolConcurrency.WorkspaceExclusive"/> 只取工作区执行权。
    /// </summary>
    /// <remarks>
    /// 两级共用同一把工作区执行权，所以同一工作区内的<em>任何</em>两个调用都不会重叠执行——
    /// 若只给两级各自一把锁，串行工具就能与独占工具并行，等于取消没有真正生效。
    /// 跨工作区协调仍属后续扩展：这里的全局串行权只保证保守默认，不做跨工作区资源仲裁。
    /// </remarks>
    public async ValueTask<IDisposable> EnterAsync(
        ToolExecutionPlan plan, string workspace, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        SemaphoreSlim gate;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_workspaceLocks.TryGetValue(workspace, out gate!))
            {
                gate = new SemaphoreSlim(1, 1);
                _workspaceLocks[workspace] = gate;
            }
        }

        if (plan.Concurrency == ToolConcurrency.Serial)
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _serial.Release();
                throw;
            }

            return new Releaser(gate, _serial);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serial.Dispose();
        lock (_sync)
        {
            foreach (var gate in _workspaceLocks.Values) gate.Dispose();
            _workspaceLocks.Clear();
        }
    }

    /// <summary>
    /// 释放顺序与取得顺序相反：先放工作区执行权，再放全局串行权。
    /// </summary>
    private sealed class Releaser(SemaphoreSlim gate, SemaphoreSlim? outer = null) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            gate.Release();
            outer?.Release();
        }
    }
}
