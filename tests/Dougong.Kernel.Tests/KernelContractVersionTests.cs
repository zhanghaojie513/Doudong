using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

public sealed class KernelContractVersionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string ContractsAssemblyPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "Dougong.Sample.Contracts", "bin", Configuration,
            "net10.0", "Dougong.Sample.Contracts.dll"));

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Fact]
    public void Host_contract_version_is_exposed()
    {
        Assert.True(DougongKernel.ContractVersions.TryGetValue("Dougong.Kernel", out var version));
        Assert.NotNull(version);
    }

    [Theory]
    [InlineData("0.1", true)]
    [InlineData("0.0", true)]
    [InlineData("0.2", false)]
    [InlineData("1.0", false)]
    public void Contract_range_is_checked_against_host_major_and_minor(string range, bool expected)
    {
        var host = DougongKernel.ContractVersions["Dougong.Kernel"];

        Assert.Equal(expected, new ContractVersion("Dougong.Kernel", range).IsSatisfiedBy(host));
    }

    [Fact]
    public void Invalid_contract_range_is_rejected_at_manifest_construction()
    {
        Assert.Throws<FormatException>(() => PluginManifest.CreateWithContracts(
            "test.bad", "1.0.0", [new ContractVersion("Dougong.Kernel", "not-a-version")]));
    }

    [Fact]
    public async Task Compatible_contract_plugin_loads()
    {
        await using var kernel = new DougongKernel();
        Assert.True(File.Exists(ContractsAssemblyPath), $"契约样例未构建：{ContractsAssemblyPath}");

        var handle = await kernel.LoadAsync(
            ContractsAssemblyPath, "Dougong.Sample.Contracts.CompatibleContractPlugin")
            .AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Active, handle.State);
    }

    [Theory]
    [InlineData("Dougong.Sample.Contracts.IncompatibleContractPlugin", "999.0")]
    [InlineData("Dougong.Sample.Contracts.UnknownContractPlugin", "Dougong.NotProvided")]
    public async Task Incompatible_contract_plugin_is_rejected(string typeName, string messagePart)
    {
        await using var kernel = new DougongKernel();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.LoadAsync(ContractsAssemblyPath, typeName).AsTask().WaitAsync(Timeout));

        Assert.Contains(messagePart, error.Message);
        Assert.Empty(await kernel.GetPluginsAsync());
    }

    [Fact]
    public async Task Manifest_without_contract_declarations_is_not_restricted()
    {
        await using var kernel = new DougongKernel();
        Assert.True(File.Exists(SampleAssemblyPath), $"样例插件未构建：{SampleAssemblyPath}");

        var handle = await kernel.LoadAsync(SampleAssemblyPath, "Dougong.Sample.Plugin.SamplePlugin")
            .AsTask().WaitAsync(Timeout);

        Assert.Equal(PluginState.Active, handle.State);
    }

    private static string SampleAssemblyPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "Dougong.Sample.Plugin", "bin", Configuration,
            "net10.0", "Dougong.Sample.Plugin.dll"));
}
