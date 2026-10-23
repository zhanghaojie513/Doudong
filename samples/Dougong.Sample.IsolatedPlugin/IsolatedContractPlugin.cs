using Dougong.Kernel;
using Dougong.Sample.Isolated;

namespace Dougong.Sample.IsolatedPlugin;

/// <summary>
/// 使用独立契约程序集的插件。契约类型只出现在激活方法体里，
/// 因此宿主无法靠类型签名推断依赖，必须真实解析装配才能发现问题。
/// </summary>
public sealed class IsolatedContractPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create("dougong.sample.isolated", "1.0.0");

    public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
    {
        context.Services.Register<IIsolatedProbe>(new IsolatedProbe());
        return ValueTask.CompletedTask;
    }

    private sealed class IsolatedProbe : IIsolatedProbe
    {
        public string Describe() => "isolated";
    }
}
