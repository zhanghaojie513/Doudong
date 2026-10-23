namespace Dougong.Kernel;

/// <summary>
/// 记录消费方代次实际解析过的提供方，用于停机时的依赖排序与失效传播。
/// 只保存 ID 与代次，不保存 Type、委托或服务实例。
/// </summary>
public sealed partial class DougongKernel
{
    private readonly Dictionary<(PluginId Consumer, long Generation), HashSet<(PluginId Owner, long Generation)>>
        _usedProviders = [];

    /// <summary>
    /// 记录一条运行时服务依赖边；调用方须持有 <c>_sync</c>。
    /// 自身解析不算跨插件依赖；合并声明依赖后成环则拒绝并报告路径。
    /// </summary>
    internal void RecordDependency(
        PluginId consumer, long consumerGeneration, PluginId provider, long providerGeneration)
    {
        if (consumer == provider) return;
        if (FindRuntimeCycle(consumer, provider) is { } cycle)
            throw new InvalidOperationException(
                $"服务依赖成环：{string.Join(" -> ", cycle)}。");

        if (!_usedProviders.TryGetValue((consumer, consumerGeneration), out var providers))
        {
            providers = [];
            _usedProviders[(consumer, consumerGeneration)] = providers;
        }

        providers.Add((provider, providerGeneration));
    }

    /// <summary>该代次停用并清理成功后调用，丢弃可能指向已释放资源的旧边。</summary>
    internal void ForgetDependencies(PluginId consumer, long generation) =>
        _usedProviders.Remove((consumer, generation));

    /// <summary>消费方当前代次曾使用的提供方是否已不在原代次上可用。</summary>
    internal bool HasUnavailableUsedProvider(PluginEntry consumer)
    {
        if (!_usedProviders.TryGetValue((consumer.Id, consumer.Generation), out var providers))
            return false;

        foreach (var (owner, generation) in providers)
        {
            if (!_entries.TryGetValue(owner, out var entry)
                || entry.Generation != generation
                || entry.State != PluginState.Active
                || entry.Context is not { IsClosed: false })
                return true;
        }

        return false;
    }

    /// <summary>
    /// 停用批次排序：消费者先于它依赖的提供方（声明依赖与实际使用边合并）。
    /// 无依赖约束的节点按逆激活序稳定排列。
    /// </summary>
    private void SortStopBatch(List<(PluginEntry Entry, PluginContext Context)> batch)
    {
        var byId = batch.ToDictionary(b => b.Entry.Id, b => b.Entry);
        var dependents = new Dictionary<PluginId, HashSet<PluginId>>();
        foreach (var (entry, _) in batch) dependents[entry.Id] = [];

        foreach (var (entry, _) in batch)
        {
            foreach (var declaration in entry.Manifest.DependsOn)
            {
                if (declaration != entry.Id && byId.ContainsKey(declaration))
                    dependents[declaration].Add(entry.Id);
            }

            if (_usedProviders.TryGetValue((entry.Id, entry.Generation), out var used))
            {
                foreach (var (owner, generation) in used)
                {
                    if (owner == entry.Id) continue;
                    if (byId.TryGetValue(owner, out var provider) && provider.Generation == generation)
                        dependents[owner].Add(entry.Id);
                }
            }
        }

        // 没有仍存活消费者依赖它的节点先清理，因此消费者先于提供方。
        var remaining = batch.ToDictionary(b => b.Entry.Id, b => b.Entry);
        var ordered = new List<(PluginEntry Entry, PluginContext Context)>(batch.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(candidate => dependents[candidate.Id].All(d => !remaining.ContainsKey(d)))
                .ToList();
            if (ready.Count == 0)
            {
                // 环已在登记处拒绝；若仍遇到异常图，退回逆激活序以保证有界完成。
                ready = [.. remaining.Values];
            }

            var next = ready.OrderByDescending(e => e.ActivationSequence).First();
            ordered.Add(batch.First(b => ReferenceEquals(b.Entry, next)));
            remaining.Remove(next.Id);
        }

        batch.Clear();
        batch.AddRange(ordered);
    }

    /// <summary>若 provider 的依赖闭包回到 consumer，返回闭环路径。</summary>
    private List<PluginId>? FindRuntimeCycle(PluginId consumer, PluginId provider)
    {
        var path = new List<PluginId> { provider };
        var onPath = new HashSet<PluginId> { provider };
        return Walk(provider) ? [.. path, consumer, provider] : null;

        bool Walk(PluginId current)
        {
            foreach (var dependency in DependenciesOf(current))
            {
                if (dependency == consumer) return true;
                if (!onPath.Add(dependency)) continue;
                path.Add(dependency);
                if (Walk(dependency)) return true;
                path.RemoveAt(path.Count - 1);
                onPath.Remove(dependency);
            }

            return false;
        }

        IEnumerable<PluginId> DependenciesOf(PluginId id)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                foreach (var dependency in entry.Manifest.DependsOn) yield return dependency;
                if (_usedProviders.TryGetValue((id, entry.Generation), out var used))
                    foreach (var (owner, _) in used) yield return owner;
            }
        }
    }
}
