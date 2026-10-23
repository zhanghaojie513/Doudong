using Dougong.Kernel;

namespace Dougong.Sample.Plugin;

/// <summary>示例插件：注册一个可解析服务，并用清理回调证明停用路径被执行。</summary>
public sealed class SamplePlugin : IPlugin
{
    public static int Activations;

    public PluginManifest Manifest { get; } = PluginManifest.Create("dougong.sample.plugin", "1.0.0");

    public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Activations++;
        context.Services.Register<ISampleService>(new SampleService());
        context.RegisterForCleanup(_ =>
        {
            CleanupCount++;
            return ValueTask.CompletedTask;
        });
        return ValueTask.CompletedTask;
    }

    public static int CleanupCount { get; private set; }
}

public interface ISampleService
{
    string Describe();
}

internal sealed class SampleService : ISampleService
{
    public string Describe() => "sample";
}
