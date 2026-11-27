using System.IO;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

/// <summary>
/// Cross-machine SolidWorks model workspace resolver.
/// Resolves bundled release models (&lt;AppBase&gt;\Models\初始模型) or dev-repo models automatically
/// without requiring any hardcoded developer drive paths in Config/appsettings.json.
/// </summary>
public static class WorkspacePathResolver
{
    public const string DefaultAssemblyRelativePath = @"总装配\总装配体.SLDASM";

    public static SolidWorksWorkspaceOptions ResolveWorkspaceOptions(SolidWorksWorkspaceOptions? rawOptions)
    {
        var source = rawOptions ?? new SolidWorksWorkspaceOptions();
        var assemblyName = string.IsNullOrWhiteSpace(source.AssemblyFileName)
            ? DefaultAssemblyRelativePath
            : source.AssemblyFileName.Trim();

        // 1. Respect valid explicit InitialModelFolder (keeps unit tests & explicit custom deployments untouched)
        if (!string.IsNullOrWhiteSpace(source.InitialModelFolder) &&
            Path.IsPathFullyQualified(source.InitialModelFolder) &&
            Directory.Exists(source.InitialModelFolder))
        {
            var initial = Path.GetFullPath(source.InitialModelFolder);
            var working = ResolveWorkingFolderForInitial(source.WorkingModelFolder, initial);
            var root = !string.IsNullOrWhiteSpace(source.ModelRootPath) && Directory.Exists(source.ModelRootPath)
                ? Path.GetFullPath(source.ModelRootPath)
                : (Directory.GetParent(initial)?.FullName ?? initial);

            return new SolidWorksWorkspaceOptions
            {
                ModelRootPath = root,
                InitialModelFolder = initial,
                WorkingModelFolder = working,
                AssemblyFileName = assemblyName
            };
        }

        // 2. Auto-discover bundled portable release folder or dev repository folder
        var discoveredInitial = DiscoverInitialModelFolder(assemblyName);
        if (!string.IsNullOrWhiteSpace(discoveredInitial))
        {
            var root = Directory.GetParent(discoveredInitial)?.FullName ?? discoveredInitial;
            var working = ResolveWorkingFolderForInitial(source.WorkingModelFolder, discoveredInitial);
            return new SolidWorksWorkspaceOptions
            {
                ModelRootPath = root,
                InitialModelFolder = discoveredInitial,
                WorkingModelFolder = working,
                AssemblyFileName = assemblyName
            };
        }

        // 3. Fallback preserving raw options (with clean default assembly name)
        return new SolidWorksWorkspaceOptions
        {
            ModelRootPath = source.ModelRootPath ?? string.Empty,
            InitialModelFolder = source.InitialModelFolder ?? string.Empty,
            WorkingModelFolder = source.WorkingModelFolder ?? string.Empty,
            AssemblyFileName = assemblyName
        };
    }

    public static string? ResolveInitialAssemblyPath(SolidWorksWorkspaceOptions? rawOptions = null)
    {
        var resolved = ResolveWorkspaceOptions(rawOptions ?? AppConfigLoader.LoadSettings().SolidWorksWorkspace);
        if (string.IsNullOrWhiteSpace(resolved.InitialModelFolder))
        {
            return null;
        }

        var candidate = Path.Combine(resolved.InitialModelFolder, resolved.AssemblyFileName);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    internal static string? DiscoverInitialModelFolder(string assemblyFileName, IEnumerable<string>? searchAnchors = null)
    {
        var relCandidates = new[]
        {
            @"Models\初始模型",
            @"初始模型",
            @"不伸缩单层检查车\Solidworks模型\初始模型",
            @"Solidworks模型\初始模型"
        };

        var anchors = searchAnchors?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                      ?? [AppPaths.BaseDirectory, AppPaths.ProjectRoot, Directory.GetCurrentDirectory()];

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ancestorDirs = new List<DirectoryInfo>();
        foreach (var anchor in anchors)
        {
            DirectoryInfo? current;
            try
            {
                current = new DirectoryInfo(Path.GetFullPath(anchor));
            }
            catch
            {
                continue;
            }

            for (var depth = 0; depth <= 6 && current is not null; depth++)
            {
                if (visited.Add(current.FullName))
                {
                    ancestorDirs.Add(current);
                    var directHit = TryMatchUnderDirectory(current.FullName, relCandidates, assemblyFileName);
                    if (directHit is not null)
                    {
                        return directHit;
                    }
                }

                current = current.Parent;
            }
        }

        // Out-of-tree build output support (e.g. <Root>\构建输出\... sibling of <Root>\反力架\三六重工v2\...):
        // scan up to 2 child levels beneath each non-root ancestor directory (fast, bounded, zero hardcoded paths).
        foreach (var ancestor in ancestorDirs)
        {
            if (ancestor.Parent is null) continue; // skip bare drive roots like C:\ or E:\
            var siblingHit = TryMatchInBoundedChildren(ancestor.FullName, relCandidates, assemblyFileName, maxDepth: 2);
            if (siblingHit is not null)
            {
                return siblingHit;
            }
        }

        return null;
    }

    private static string? TryMatchUnderDirectory(string baseDir, string[] relCandidates, string assemblyFileName)
    {
        foreach (var rel in relCandidates)
        {
            var folder = Path.Combine(baseDir, rel);
            if (Directory.Exists(folder) && File.Exists(Path.Combine(folder, assemblyFileName)))
            {
                return Path.GetFullPath(folder);
            }
        }
        return null;
    }

    private static string? TryMatchInBoundedChildren(string dir, string[] relCandidates, string assemblyFileName, int maxDepth)
    {
        if (maxDepth <= 0 || !Directory.Exists(dir)) return null;
        try
        {
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') ||
                    name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("构建输出", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hit = TryMatchUnderDirectory(child, relCandidates, assemblyFileName);
                if (hit is not null) return hit;

                if (maxDepth > 1)
                {
                    var nestedHit = TryMatchInBoundedChildren(child, relCandidates, assemblyFileName, maxDepth - 1);
                    if (nestedHit is not null) return nestedHit;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ResolveWorkingFolderForInitial(string? configuredWorking, string resolvedInitial)
    {
        if (!string.IsNullOrWhiteSpace(configuredWorking) && Path.IsPathFullyQualified(configuredWorking))
        {
            var full = Path.GetFullPath(configuredWorking);
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) &&
                !string.Equals(full, resolvedInitial, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
        }

        // Dev-repo sibling "新模型" if parent directory is NOT inside Program Files and is writable
        var initialParent = Directory.GetParent(resolvedInitial)?.FullName;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var underProgramFiles = (!string.IsNullOrWhiteSpace(programFiles) && resolvedInitial.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrWhiteSpace(programFilesX86) && resolvedInitial.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase));

        if (!underProgramFiles && !string.IsNullOrWhiteSpace(initialParent) &&
            initialParent.EndsWith("Solidworks模型", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(initialParent, "新模型"));
        }

        // Portable / installed release: guaranteed-writable per-user workspace under %LocalAppData%
        return Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SanliuBridgeCar",
            "Workspace",
            "DefaultModel"));
    }
}
