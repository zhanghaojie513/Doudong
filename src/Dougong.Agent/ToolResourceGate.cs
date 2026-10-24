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
    /// 取得执行权。<see cref="ToolConcurrency.Serial"/> 走全局串行信号量，
    /// <see cref="ToolConcurrency.WorkspaceExclusive"/> 走按工作区的独占信号量。
    /// </summary>
    public async ValueTask<IDisposable> EnterAsync(
        ToolExecutionPlan plan, string workspace, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        if (plan.Concurrency == ToolConcurrency.WorkspaceExclusive)
        {
            SemaphoreSlim gate;
            lock (_sync)
            {
                if (!_workspaceLocks.TryGetValue(workspace, out gate!))
                {
                    gate = new SemaphoreSlim(1, 1);
                    _workspaceLocks[workspace] = gate;
                }
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser(gate);
        }

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_serial);
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

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
