namespace Dougong.Kernel;

/// <summary>
/// 一次原子取得的状态视图：不可变插件快照 + 修订号 + 已衔接的增量流。
/// 先应用 <see cref="Snapshot"/>，再消费 <see cref="Changes"/>；两者之间不会丢失或重复状态。
/// </summary>
public sealed class KernelStateView : IDisposable
{
    internal KernelStateView(
        long revision,
        IReadOnlyList<PluginHandle>? snapshot,
        IAsyncEnumerable<StateDelta> changes,
        IDisposable subscription,
        bool requiresResync = false)
    {
        Revision = revision;
        Snapshot = snapshot;
        Changes = changes;
        Subscription = subscription;
        RequiresResync = requiresResync;
    }

    /// <summary>快照（或续订游标）对应的内核修订号。</summary>
    public long Revision { get; }

    /// <summary>当前状态快照；从游标续订时为 null，调用方应保留自己的状态。</summary>
    public IReadOnlyList<PluginHandle>? Snapshot { get; }

    /// <summary>
    /// 修订号严格递增的后续状态增量。同一插件的增量按分区顺序递增；
    /// 不同插件之间不承诺可比较的业务顺序，只能依赖修订号。
    /// </summary>
    public IAsyncEnumerable<StateDelta> Changes { get; }

    /// <summary>请求的游标早于保留窗口，必须先重新取完整快照。</summary>
    public bool RequiresResync { get; }

    public IDisposable Subscription { get; }

    public void Dispose() => Subscription.Dispose();
}

/// <summary>
/// 一条带修订号的状态增量。<see cref="ResyncRequired"/> 为 true 时须重新取快照。
/// <see cref="Partition"/> 是状态所属的分区（插件）；同一分区内修订号严格递增。
/// </summary>
public sealed record StateDelta(
    long Revision,
    PluginStateChange? Change,
    bool ResyncRequired = false,
    PluginId? Partition = null,
    StateDeltaKind Kind = StateDeltaKind.StateChange,
    PluginState? State = null);