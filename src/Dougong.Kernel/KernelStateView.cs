namespace Dougong.Kernel;

/// <summary>
/// 一次原子取得的状态视图：不可变插件快照 + 修订号 + 已衔接的增量流。
/// 先应用 <see cref="Snapshot"/>，再消费 <see cref="Changes"/>；两者之间不会丢失或重复状态。
/// </summary>
public sealed class KernelStateView : IDisposable
{
    internal KernelStateView(
        long revision,
        IReadOnlyList<PluginHandle> snapshot,
        IAsyncEnumerable<StateDelta> changes,
        IDisposable subscription)
    {
        Revision = revision;
        Snapshot = snapshot;
        Changes = changes;
        Subscription = subscription;
    }

    /// <summary>快照对应的内核修订号。</summary>
    public long Revision { get; }

    public IReadOnlyList<PluginHandle> Snapshot { get; }

    /// <summary>修订号严格递增的后续状态增量；溢出时序列以 <see cref="StateDelta.ResyncRequired"/> 结束。</summary>
    public IAsyncEnumerable<StateDelta> Changes { get; }

    public IDisposable Subscription { get; }

    public void Dispose() => Subscription.Dispose();
}

/// <summary>一条带修订号的状态增量。<see cref="ResyncRequired"/> 为 true 时须重新取快照。</summary>
public sealed record StateDelta(long Revision, PluginStateChange? Change, bool ResyncRequired = false);
