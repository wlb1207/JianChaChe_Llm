using System.IO;
namespace LlmWpfPrototype.Services;

/// <summary>Fail-closed workspace deletion and write paths. Never redirects references.</summary>
public static class WorkspaceSafetyPolicy
{
    public const string MarkerName = ".llm-wpf-workspace";
    public static string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("安全保护：路径必须是明确的绝对路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    public static bool Within(string path, string root) =>
        string.Equals(Full(path), Full(root), StringComparison.OrdinalIgnoreCase) ||
        Full(path).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void RejectLinks(string path)
    {
        var current = Full(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"安全保护：拒绝目录链接或重解析点：{current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
    public static void ValidateLayout(string initial, string working, string assembly, string? program = null)
    {
        initial = Full(initial); working = Full(working); program = Full(program ?? AppContext.BaseDirectory);
        RejectLinks(initial); RejectLinks(working);
        if (working == Path.GetPathRoot(working) || Within(initial, working) || Within(working, initial) ||
            Within(program, working) || Within(working, program) || Within(initial, program) || Within(program, initial))
            throw new InvalidOperationException("安全保护：初始模型、专用工作区和程序目录不得相同或互相包含。");
        if (string.IsNullOrWhiteSpace(assembly) || Path.IsPathRooted(assembly) ||
            !Within(Path.GetFullPath(Path.Combine(working, assembly)), working))
            throw new InvalidOperationException("安全保护：装配相对路径越出工作区。");
        if (!Directory.Exists(initial)) throw new DirectoryNotFoundException($"初始模型目录不存在：{initial}");
        if (!File.Exists(Path.Combine(initial, assembly)))
            throw new FileNotFoundException("初始主装配不存在。", Path.Combine(initial, assembly));
    }
    public static void ValidateTree(string root)
    {
        RejectLinks(root);
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"安全保护：模型目录含重解析点：{entry}");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
    }
    private static string Identity(string initial, string working) =>
        "LlmWpfPrototype workspace v1\n" + Full(initial).ToUpperInvariant() + "\n" + Full(working).ToUpperInvariant();
    public static void RequireOwnedOrEmpty(string initial, string working)
    {
        ValidateTree(working);
        if (!Directory.Exists(working) || !Directory.EnumerateFileSystemEntries(working).Any()) return;
        var marker = Path.Combine(working, MarkerName);
        if (!File.Exists(marker) || File.ReadAllText(marker) != Identity(initial, working))
            throw new InvalidOperationException($"安全保护：拒绝删除或写入未登记的非空工作目录：{working}");
    }
    public static void MarkOwned(string initial, string working)
    {
        using var stream = new FileStream(Path.Combine(working, MarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(Identity(initial, working));
    }
    public static void RequireWritableModel(string path, string initial, string working)
    {
        path = Full(path); initial = Full(initial); working = Full(working);
        RejectLinks(path); RejectLinks(working);
        if (!Within(path, working) || Within(path, initial) || !File.Exists(path))
            throw new InvalidOperationException($"安全保护：实际模型不在已批准工作副本内或文件缺失：{path}");
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new InvalidOperationException($"安全保护：模型文件只读：{path}");
        RequireOwnedOrEmpty(initial, working);
    }
}
