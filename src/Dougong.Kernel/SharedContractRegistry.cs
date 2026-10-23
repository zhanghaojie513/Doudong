namespace Dougong.Kernel;

/// <summary>
/// 宿主与动态插件共享的契约程序集注册表。登记是加载的前置条件：
/// 声明了共享契约类型的插件，只有在对应程序集已登记时才能加载。
/// 登记按内核实例隔离，可撤销，避免测试或模块之间互相污染。
/// </summary>
public sealed class SharedContractRegistry
{
    private readonly object _sync = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyCollection<string> Names
    {
        get { lock (_sync) { return [.. _names]; } }
    }

    internal bool Contains(string name)
    {
        lock (_sync) { return _names.Contains(name); }
    }

    /// <summary>
    /// 登记一个契约程序集。返回的句柄释放后撤销登记；重复登记相互独立。
    /// </summary>
    public IDisposable Register(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var name = assembly.GetName().Name
            ?? throw new ArgumentException("契约程序集没有名称。", nameof(assembly));

        lock (_sync) { _names.Add(name); }
        return new Registration(this, name);
    }

    private void Unregister(string name)
    {
        lock (_sync) { _names.Remove(name); }
    }

    private sealed class Registration(SharedContractRegistry owner, string name) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unregister(name);
        }
    }
}
