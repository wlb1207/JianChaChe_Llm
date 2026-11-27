using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LlmWpfPrototype.Services;

public static class CadReferenceBoundary
{
    private const string LegacyInspectionCarRoot = @"E:\智能检查车项目\不伸缩单层检查车";
    private const string LegacyFixedGateRoot = @"E:\智能检查车项目\固定门架";
    internal const string RebindCacheFileName = ".workspace-rebind-verified.json";
    internal const string ForkOriginFileName = ".workspace-fork-origin.json";
    private const int RebindCacheSchemaVersion = 1;

    private sealed record AssemblyFingerprint(string RelativePath, long Length, long LastWriteTimeUtcTicks);

    private sealed record WorkspaceRebindCacheStamp(
        int SchemaVersion,
        string WorkingFolder,
        string AssemblyPath,
        int ReboundCount,
        DateTime VerifiedAtUtc,
        List<AssemblyFingerprint> Assemblies);

    private sealed record WorkspaceForkOriginRecord(string SourceWorkspaceFolder, DateTime CreatedAtUtc);

    public static bool IsWorkspaceMetadataFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        (string.Equals(fileName, RebindCacheFileName, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(fileName, ForkOriginFileName, StringComparison.OrdinalIgnoreCase));

    public static void WriteForkOrigin(string workingFolder, string sourceWorkspaceFolder)
    {
        if (string.IsNullOrWhiteSpace(workingFolder) || string.IsNullOrWhiteSpace(sourceWorkspaceFolder))
        {
            return;
        }

        try
        {
            var normalizedWorking = WorkspaceSafetyPolicy.Full(workingFolder);
            var normalizedSource = WorkspaceSafetyPolicy.Full(sourceWorkspaceFolder);
            if (string.Equals(normalizedWorking, normalizedSource, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(normalizedWorking);
            var path = Path.Combine(normalizedWorking, ForkOriginFileName);
            var record = new WorkspaceForkOriginRecord(normalizedSource, DateTime.UtcNow);
            File.WriteAllText(path, JsonSerializer.Serialize(record), new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    public static string? TryReadForkOrigin(string workingFolder)
    {
        if (string.IsNullOrWhiteSpace(workingFolder))
        {
            return null;
        }

        try
        {
            var normalizedWorking = WorkspaceSafetyPolicy.Full(workingFolder);
            var path = Path.Combine(normalizedWorking, ForkOriginFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            WorkspaceSafetyPolicy.RejectLinks(path);
            var record = JsonSerializer.Deserialize<WorkspaceForkOriginRecord>(File.ReadAllText(path, Encoding.UTF8));
            if (record is null || string.IsNullOrWhiteSpace(record.SourceWorkspaceFolder) ||
                !Path.IsPathFullyQualified(record.SourceWorkspaceFolder))
            {
                return null;
            }

            var normalizedSource = WorkspaceSafetyPolicy.Full(record.SourceWorkspaceFolder);
            return string.Equals(normalizedSource, normalizedWorking, StringComparison.OrdinalIgnoreCase)
                ? null
                : normalizedSource;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryValidateRebindCache(
        string assembly,
        string working,
        string initial,
        out int verifiedAssemblyCount,
        Action<string>? log = null)
    {
        verifiedAssemblyCount = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(working) || string.IsNullOrWhiteSpace(initial))
            {
                return false;
            }

            if (WorkspaceSafetyPolicy.Within(initial, working) || WorkspaceSafetyPolicy.Within(working, initial))
            {
                return false;
            }

            var normalizedWorking = WorkspaceSafetyPolicy.Full(working);
            var normalizedAssembly = Path.GetFullPath(assembly);
            if (!WorkspaceSafetyPolicy.Within(normalizedAssembly, normalizedWorking) || !File.Exists(normalizedAssembly))
            {
                return false;
            }

            var cachePath = Path.Combine(normalizedWorking, RebindCacheFileName);
            if (!File.Exists(cachePath))
            {
                return false;
            }

            WorkspaceSafetyPolicy.RejectLinks(cachePath);
            WorkspaceSafetyPolicy.RejectLinks(normalizedAssembly);

            var stamp = JsonSerializer.Deserialize<WorkspaceRebindCacheStamp>(File.ReadAllText(cachePath, Encoding.UTF8));
            if (stamp is null ||
                stamp.SchemaVersion != RebindCacheSchemaVersion ||
                !string.Equals(stamp.WorkingFolder, normalizedWorking, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(stamp.AssemblyPath, normalizedAssembly, StringComparison.OrdinalIgnoreCase) ||
                stamp.Assemblies is not { Count: > 0 })
            {
                log?.Invoke("Workspace rebind cache rejected: directory or target assembly metadata changed.");
                return false;
            }

            var liveAssemblies = CollectAssemblyFingerprints(normalizedWorking);
            if (liveAssemblies.Count != stamp.Assemblies.Count)
            {
                log?.Invoke($"Workspace rebind cache invalidated: assembly count changed ({stamp.Assemblies.Count} -> {liveAssemblies.Count}).");
                return false;
            }

            for (var i = 0; i < liveAssemblies.Count; i++)
            {
                var live = liveAssemblies[i];
                var cached = stamp.Assemblies[i];
                if (!string.Equals(live.RelativePath, cached.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                    live.Length != cached.Length ||
                    live.LastWriteTimeUtcTicks != cached.LastWriteTimeUtcTicks)
                {
                    log?.Invoke($"Workspace rebind cache invalidated: assembly modified ({live.RelativePath}).");
                    return false;
                }
            }

            verifiedAssemblyCount = liveAssemblies.Count;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void WriteRebindCache(
        string assembly,
        string working,
        int reboundCount,
        Action<string>? log = null)
    {
        try
        {
            var normalizedWorking = WorkspaceSafetyPolicy.Full(working);
            var normalizedAssembly = Path.GetFullPath(assembly);
            if (!WorkspaceSafetyPolicy.Within(normalizedAssembly, normalizedWorking) || !File.Exists(normalizedAssembly))
            {
                return;
            }

            var assemblies = CollectAssemblyFingerprints(normalizedWorking);
            if (assemblies.Count == 0)
            {
                return;
            }

            var stamp = new WorkspaceRebindCacheStamp(
                RebindCacheSchemaVersion,
                normalizedWorking,
                normalizedAssembly,
                reboundCount,
                DateTime.UtcNow,
                assemblies);
            var cachePath = Path.Combine(normalizedWorking, RebindCacheFileName);
            var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(stamp), new UTF8Encoding(false));
            File.Move(tempPath, cachePath, overwrite: true);

            var forkOriginPath = Path.Combine(normalizedWorking, ForkOriginFileName);
            if (File.Exists(forkOriginPath))
            {
                try { File.Delete(forkOriginPath); } catch { }
            }

            log?.Invoke($"Workspace rebind cache written. Assemblies={assemblies.Count}; Rebound={reboundCount}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Workspace rebind cache write skipped: {ex.Message}");
        }
    }

    private static List<AssemblyFingerprint> CollectAssemblyFingerprints(string normalizedWorking)
    {
        var result = new List<AssemblyFingerprint>();
        foreach (var file in Directory.EnumerateFiles(normalizedWorking, "*.sldasm", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(normalizedWorking, file);
            var segments = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(seg =>
                string.Equals(seg, "初始模型", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(seg, "backup", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            WorkspaceSafetyPolicy.RejectLinks(file);
            var info = new FileInfo(file);
            result.Add(new AssemblyFingerprint(rel, info.Length, info.LastWriteTimeUtc.Ticks));
        }

        result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return result;
    }

    public static int RebindWorkingAssembly(string assembly, string working, string initial,
        Func<string, object?> readDependencies, Func<string, string, string, bool> replaceDependency,
        Func<string, bool> isOpen, Action<string>? log,
        CancellationToken cancellationToken = default,
        Action<int, int, string>? progress = null)
    {
        if (WorkspaceSafetyPolicy.Within(initial, working) || WorkspaceSafetyPolicy.Within(working, initial))
            throw new InvalidOperationException("初始模型和工作副本目录必须互相独立。");
        var forkSourceRoot = TryReadForkOrigin(working);
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(string Owner, string OldPath, string NewPath)>();
        IReadOnlyList<object> Read(string owner)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = readDependencies(owner);
            if (raw is not Array array || array.Rank != 1 || array.Length == 0 || array.Length % 2 != 0)
                throw new InvalidOperationException($"无法取得装配保存的组件引用：{owner}");
            return array.Cast<object>().ToArray();
        }
        void RequireWorkingFile(string path)
        {
            if (!WorkspaceSafetyPolicy.Within(path, working) || !File.Exists(path))
                throw new FileNotFoundException($"工作副本缺少装配引用：{path}", path);
            WorkspaceSafetyPolicy.RejectLinks(path);
        }

        pending.Enqueue(Path.GetFullPath(assembly));
        while (pending.TryDequeue(out var owner))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(owner)) continue;
            RequireWorkingFile(owner);
            var pairs = Read(owner);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < pairs.Count; index += 2)
            {
                var original = pairs[index] as string ?? string.Empty;
                if (!seen.Add(original)) continue;
                var target = MapCopiedReferencePath(original, working, initial, forkSourceRoot);
                RequireWorkingFile(target);
                if (!string.Equals(Path.GetFullPath(original), target, StringComparison.OrdinalIgnoreCase))
                    plan.Add((owner, original, target));
                if (string.Equals(Path.GetExtension(target), ".SLDASM", StringComparison.OrdinalIgnoreCase))
                    pending.Enqueue(target);
            }
        }

        foreach (var owner in plan.Select(change => change.Owner).Distinct(StringComparer.OrdinalIgnoreCase))
            if (isOpen(owner))
                throw new InvalidOperationException($"工作装配已打开且仍引用初始模型。请关闭这个工作装配后重新打开：{owner}");

        var reboundIndex = 0;
        var totalRebinds = plan.Count;
        foreach (var (owner, oldPath, newPath) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!replaceDependency(owner, oldPath, newPath))
                throw new InvalidOperationException($"SolidWorks 未能更新工作装配引用：{owner} -> {newPath}");
            reboundIndex++;
            progress?.Invoke(reboundIndex, totalRebinds, Path.GetFileName(newPath));
            log?.Invoke($"Working assembly reference rebound. Owner={owner}; Target={newPath}");
        }
        foreach (var owner in plan.Select(change => change.Owner).Distinct(StringComparer.OrdinalIgnoreCase))
            ValidateSavedPairs(owner, working, initial, Read(owner), File.Exists);

        log?.Invoke($"Working assembly references verified. Assemblies={visited.Count}; Rebound={plan.Count}");
        return plan.Count;
    }

    public static string MapCopiedReferencePath(string reference, string working, string initial, string? forkSourceRoot = null)
    {
        if (!Path.IsPathFullyQualified(reference))
            throw new InvalidOperationException($"装配引用不是绝对路径：{reference}");
        var fullReference = Path.GetFullPath(reference);
        working = WorkspaceSafetyPolicy.Full(working);
        initial = WorkspaceSafetyPolicy.Full(initial);
        if (WorkspaceSafetyPolicy.Within(fullReference, working)) return fullReference;
        var fromInitial = WorkspaceSafetyPolicy.Within(fullReference, initial);
        var fromLegacyGate = WorkspaceSafetyPolicy.Within(fullReference, LegacyFixedGateRoot);
        var fromLegacyCar = WorkspaceSafetyPolicy.Within(fullReference, LegacyInspectionCarRoot);
        var fromForkSource = !string.IsNullOrWhiteSpace(forkSourceRoot) &&
                             WorkspaceSafetyPolicy.Within(fullReference, forkSourceRoot);
        if (!fromInitial && !fromLegacyGate && !fromLegacyCar && !fromForkSource)
            throw new InvalidOperationException($"装配引用既不在工作副本也不在初始模型目录：{fullReference}");
        var relative = fromForkSource
            ? Path.GetRelativePath(WorkspaceSafetyPolicy.Full(forkSourceRoot!), fullReference)
            : fromLegacyGate
                ? Path.Combine("门架图", "固定门架", Path.GetRelativePath(LegacyFixedGateRoot, fullReference))
                : Path.GetRelativePath(fromInitial ? initial : LegacyInspectionCarRoot, fullReference);
        if (fromForkSource)
        {
            var copiedTarget = Path.GetFullPath(Path.Combine(working, relative));
            WorkspaceSafetyPolicy.RejectLinks(copiedTarget);
            WorkspaceSafetyPolicy.RejectLinks(fullReference);
            if (!WorkspaceSafetyPolicy.Within(copiedTarget, working) || !File.Exists(copiedTarget))
                throw new FileNotFoundException($"分支工作副本缺少对应装配引用：{copiedTarget}", copiedTarget);
            if (File.Exists(fullReference))
            {
                using var sourceStream = File.OpenRead(fullReference);
                using var copiedStream = File.OpenRead(copiedTarget);
                if (!SHA256.HashData(sourceStream).AsSpan().SequenceEqual(SHA256.HashData(copiedStream)))
                    throw new InvalidOperationException($"分支引用与源工作区文件内容不同，无法自动重绑：{fullReference}");
            }
            return copiedTarget;
        }
        if (!fromInitial)
        {
            var template = Path.GetFullPath(Path.Combine(initial, relative));
            WorkspaceSafetyPolicy.RejectLinks(template);
            WorkspaceSafetyPolicy.RejectLinks(fullReference);
            if (!File.Exists(template))
                throw new FileNotFoundException($"初始模型目录缺少旧引用的对应文件：{template}", template);
            if (File.Exists(fullReference))
            {
                using var legacy = File.OpenRead(fullReference);
                using var current = File.OpenRead(template);
                if (!SHA256.HashData(legacy).AsSpan().SequenceEqual(SHA256.HashData(current)))
                    throw new InvalidOperationException($"旧引用与初始模型文件内容不同，无法自动替换：{fullReference}");
            }
        }
        var replacement = Path.GetFullPath(Path.Combine(working, relative));
        if (!WorkspaceSafetyPolicy.Within(replacement, working))
            throw new InvalidOperationException($"映射后的装配引用越出工作副本：{replacement}");
        return replacement;
    }

    public static void ValidateSavedPairs(string owner, string working, string initial,
        IReadOnlyList<object> pairs, Func<string, bool> exists, Action<string>? log = null)
    {
        if (pairs.Count % 2 != 0) throw new InvalidOperationException($"已保存引用表格式未知：{owner}");
        for (var i = 0; i < pairs.Count; i += 2)
        {
            var name = pairs[i] as string ?? "";
            var path = pairs[i + 1] as string ?? "";
            log?.Invoke($"Saved reference. Owner={owner}; Name={name}; Path={path}; SearchForMissing=False");
            if (!Path.IsPathFullyQualified(path) || !WorkspaceSafetyPolicy.Within(path, working) ||
                WorkspaceSafetyPolicy.Within(path, initial) || !exists(path))
                throw new InvalidOperationException($"外部、缺失或未确认引用，禁止打开/修改。Owner={owner}; Reference={name}; Path={path}");
            WorkspaceSafetyPolicy.RejectLinks(path);
        }
    }
}
