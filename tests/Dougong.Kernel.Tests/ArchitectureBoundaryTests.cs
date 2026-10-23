using System.Reflection;
using System.Xml.Linq;
using Dougong.Kernel;

namespace Dougong.Kernel.Tests;

/// <summary>
/// 架构边界守卫（docs/architecture.md §2、§2.1）。
/// 目标是让"SDK 与桌面端 UI 互不耦合"成为可执行断言，而不是文档约定：
/// 依赖方向、程序集引用、工程引用或内核公开 API 一旦越界，这里立即失败。
/// </summary>
public sealed class ArchitectureBoundaryTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>UI / 桌面 / 宿主专属技术栈。SDK 任何工程引用它们都算耦合。</summary>
    private static readonly string[] ForbiddenUiPrefixes =
    [
        "Avalonia",
        "AtomUI",
        "DougongDesktop",
        "ReactiveUI",
        "CommunityToolkit.Mvvm",
        "System.Windows",
        "Microsoft.AspNetCore",
    ];

    /// <summary>分层等级：数字越小越底层。引用只能指向更底层，同层不得互引。</summary>
    private static readonly Dictionary<string, int> Layers = new(StringComparer.Ordinal)
    {
        ["Dougong.Kernel"] = 0,
        ["Dougong.Agent"] = 1,
        ["Dougong.Ai"] = 2,
        ["Dougong.Mcp"] = 2,
        ["Dougong.Acp"] = 2,
        ["Dougong.Cli"] = 3,
    };

    [Fact]
    public void Kernel_references_no_ui_or_desktop_assembly()
    {
        var referenced = typeof(DougongKernel).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        var violations = referenced
            .Where(name => ForbiddenUiPrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Dougong.Kernel 不得引用 UI/桌面程序集，实际引用：{string.Join(", ", violations)}");
    }

    [Fact]
    public void Kernel_public_api_stays_in_kernel_namespace()
    {
        var offenders = typeof(DougongKernel).Assembly
            .GetExportedTypes()
            .Where(t => t.Namespace is null ||
                        !t.Namespace.StartsWith("Dougong.Kernel", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"内核公开类型必须位于 Dougong.Kernel 命名空间，越界类型：{string.Join(", ", offenders)}");
    }

    [Fact]
    public void Sdk_projects_follow_the_acyclic_dependency_direction()
    {
        var violations = new List<string>();

        foreach (var (project, references) in LoadProjectGraph())
        {
            if (!Layers.TryGetValue(project, out var ownLayer)) continue;

            foreach (var reference in references)
            {
                if (!Layers.TryGetValue(reference, out var referencedLayer)) continue;

                if (referencedLayer >= ownLayer)
                {
                    violations.Add($"{project}（L{ownLayer}）→ {reference}（L{referencedLayer}）");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "SDK 依赖方向被破坏，只允许指向更底层（Kernel ← Agent ← {Ai,Mcp,Acp} ← Cli）：" +
            string.Join("；", violations));
    }

    [Fact]
    public void No_sdk_project_references_desktop_or_ui_dependencies()
    {
        var violations = new List<string>();

        foreach (var file in AllProjectFiles())
        {
            var document = XDocument.Load(file);
            var project = Path.GetFileNameWithoutExtension(file);

            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = (string?)reference.Attribute("Include") ?? string.Empty;
                if (include.Contains("DougongDesktop", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{project} → {include}");
                }
            }

            foreach (var package in document.Descendants("PackageReference"))
            {
                var include = (string?)package.Attribute("Include") ?? string.Empty;
                if (ForbiddenUiPrefixes.Any(prefix =>
                        include.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{project} → {include}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "SDK 工程不得引用桌面端工程或 UI 依赖：" + string.Join("；", violations));
    }

    [Fact]
    public void Kernel_has_no_packages_beyond_extensions_abstractions()
    {
        var kernelProject = Path.Combine(RepoRoot, "src", "Dougong.Kernel", "Dougong.Kernel.csproj");

        var offenders = XDocument.Load(kernelProject)
            .Descendants("PackageReference")
            .Select(package => (string?)package.Attribute("Include") ?? string.Empty)
            .Where(name => !name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "内核第三方依赖只允许 Microsoft.Extensions.* 抽象，越界包：" + string.Join(", ", offenders));
    }

    private static Dictionary<string, string[]> LoadProjectGraph()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var file in SdkProjectFiles())
        {
            graph[Path.GetFileNameWithoutExtension(file)] = XDocument.Load(file)
                .Descendants("ProjectReference")
                .Select(reference => (string?)reference.Attribute("Include"))
                .Where(include => include is not null)
                .Select(include => Path.GetFileNameWithoutExtension(
                    include!.Replace('\\', Path.DirectorySeparatorChar)))
                .ToArray();
        }

        return graph;
    }

    private static IEnumerable<string> SdkProjectFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.csproj", SearchOption.AllDirectories);

    private static IEnumerable<string> AllProjectFiles() =>
        SdkProjectFiles().Concat(
            Directory.EnumerateFiles(Path.Combine(RepoRoot, "samples"), "*.csproj", SearchOption.AllDirectories));
}
