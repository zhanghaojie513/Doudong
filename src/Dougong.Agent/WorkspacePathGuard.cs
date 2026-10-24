using System.Security.Cryptography;
using System.Text;

namespace Dougong.Agent;

/// <summary>
/// 工作区路径守卫：把工具声明的路径解析到工作区内，并拒绝越界。
/// </summary>
/// <remarks>
/// 设计依据（docs/architecture.md §4.3）：路径解析到当前工作区，处理大小写、
/// 链接/重解析点及越界路径。
/// <para>
/// **这不是安全沙箱**。同一进程内可信插件可以直接调用 OS API 绕过任何检查。
/// 这里防的是模型误操作与工具实现疏漏，不是恶意插件。
/// </para>
/// </remarks>
public sealed class WorkspacePathGuard(string workspaceRoot)
{
    private readonly string _root = NormalizeRoot(workspaceRoot);

    /// <summary>规范化后的工作区根目录。</summary>
    public string Root => _root;

    /// <summary>
    /// 把 <paramref name="path"/> 解析为工作区内的绝对路径。
    /// 越界（含通过重解析点逃逸）时返回 false 并给出原因。
    /// </summary>
    public bool TryResolve(string path, out string resolved, out string reason)
    {
        resolved = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "路径为空。";
            return false;
        }

        string absolute;
        try
        {
            absolute = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(_root, path));
        }
        catch (Exception ex)
        {
            reason = $"路径无法规范化：{ex.Message}";
            return false;
        }

        // 先做名义越界检查，拦掉大部分明显错误。
        if (!IsUnderRoot(absolute))
        {
            reason = $"路径越出工作区：{absolute}";
            return false;
        }

        // 解析重解析点：符号链接/junction 可能指向工作区外。
        // 目标不存在时（新建文件）无法解析链接，按名义路径处理。
        var real = TryResolveLinks(absolute, out var realReason);
        if (real is null)
        {
            reason = realReason;
            return false;
        }

        if (!IsUnderRoot(real))
        {
            reason = $"路径经重解析点越出工作区：{real}";
            return false;
        }

        resolved = real;
        return true;
    }

    private bool IsUnderRoot(string absolute)
    {
        // 大小写不敏感比较（Windows 语义），并确保是目录前缀而非字符串前缀。
        var normalized = Path.TrimEndingDirectorySeparator(absolute);
        var root = Path.TrimEndingDirectorySeparator(_root);
        if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)) return true;
        return normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 逐段解析重解析点。返回 null 表示解析失败（例如链接目标不可达）。
    /// </summary>
    private static string? TryResolveLinks(string absolute, out string reason)
    {
        reason = string.Empty;
        try
        {
            var current = Path.TrimEndingDirectorySeparator(absolute);
            if (!File.Exists(current) && !Directory.Exists(current))
                return current;  // 新建路径：没有链接可解析

            // GetFinalPathNameByHandle 需要 P/Invoke；这里用 FileInfo 的 LinkTarget 做便携近似。
            var info = new FileInfo(current);
            if (info.Exists && info.LinkTarget is { } target)
            {
                var targetFull = Path.IsPathRooted(target)
                    ? Path.GetFullPath(target)
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, target));

                // 链接目标自身可能还是链接，递归解析但限制深度。
                for (var depth = 0; depth < 8; depth++)
                {
                    var next = new FileInfo(targetFull);
                    if (!next.Exists || next.LinkTarget is not { } nextTarget) break;
                    targetFull = Path.IsPathRooted(nextTarget)
                        ? Path.GetFullPath(nextTarget)
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(targetFull)!, nextTarget));
                }

                return Path.TrimEndingDirectorySeparator(targetFull);
            }

            return current;
        }
        catch (Exception ex)
        {
            reason = $"无法解析重解析点：{ex.Message}";
            return null;
        }
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("工作区根不能为空。", nameof(root));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
}

/// <summary>
/// 文件版本指纹。用于判断"审批时看到的文件"是否仍是"执行时的文件"。
/// </summary>
/// <remarks>
/// 指纹由长度、最后写入时间与内容哈希组成。**仍然存在竞态**：
/// 计算指纹与真正打开文件之间，文件可能被替换。真正安全的做法是在打开句柄后复核，
/// 当前实现没有做到，因此只能降低概率，不能宣称消除。
/// </remarks>
public sealed record FileVersion(string Path, long Length, DateTimeOffset LastWriteUtc, string ContentHash)
{
    /// <summary>文件不存在时的占位版本，用于区分"审批时本来就没有这个文件"。</summary>
    public static FileVersion Missing(string path) =>
        new(path, -1, DateTimeOffset.MinValue, "missing");

    public bool Exists => Length >= 0;

    /// <summary>捕获当前文件版本。文件不存在时返回 <see cref="Missing"/>。</summary>
    public static FileVersion Capture(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return Missing(path);

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new FileVersion(path, info.Length, info.LastWriteTimeUtc, hash);
    }

    /// <summary>把版本折叠为绑定用的稳定字符串。</summary>
    public string ToBindingValue() => $"{Path}|{Length}|{LastWriteUtc.ToUnixTimeSeconds()}|{ContentHash}";
}

/// <summary>
/// 一次调用依赖的文件版本集合。审批时捕获，执行前比对。
/// </summary>
public sealed record FileVersionSet(IReadOnlyList<FileVersion> Files)
{
    public static FileVersionSet Empty { get; } = new([]);

    /// <summary>按路径排序后折叠，保证顺序无关。</summary>
    public string ToBindingValue() => string.Join(";",
        Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
             .Select(f => f.ToBindingValue()));

    /// <summary>捕获一组路径的版本。</summary>
    public static FileVersionSet Capture(IEnumerable<string> paths) =>
        new([.. paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(FileVersion.Capture)]);

    /// <summary>
    /// 与执行前的当前版本比对。返回 null 表示未变化，否则返回变化说明。
    /// </summary>
    public string? Diff(FileVersionSet current)
    {
        var before = Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var after = current.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, version) in before)
        {
            if (!after.TryGetValue(path, out var now))
                return $"文件从审批时存在变为不可见：{path}";

            if (now.Length != version.Length)
                return $"文件长度变化：{path}（{version.Length} → {now.Length}）";

            if (!string.Equals(now.ContentHash, version.ContentHash, StringComparison.Ordinal))
                return $"文件内容变化：{path}";
        }

        foreach (var path in after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase))
            return $"出现了审批时未声明的文件：{path}";

        return null;
    }
}
