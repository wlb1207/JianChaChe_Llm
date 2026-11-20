using System.IO;
using System.Security.Cryptography;

namespace LlmWpfPrototype.Services;

public static class CadReferenceBoundary
{
    private const string LegacyInspectionCarRoot = @"E:\智能检查车项目\不伸缩单层检查车";
    private const string LegacyFixedGateRoot = @"E:\智能检查车项目\固定门架";

    public static int RebindWorkingAssembly(string assembly, string working, string initial,
        Func<string, object?> readDependencies, Func<string, string, string, bool> replaceDependency,
        Func<string, bool> isOpen, Action<string>? log)
    {
        if (WorkspaceSafetyPolicy.Within(initial, working) || WorkspaceSafetyPolicy.Within(working, initial))
            throw new InvalidOperationException("初始模型和工作副本目录必须互相独立。");
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(string Owner, string OldPath, string NewPath)>();
        IReadOnlyList<object> Read(string owner)
        {
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
            if (!visited.Add(owner)) continue;
            RequireWorkingFile(owner);
            var pairs = Read(owner);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < pairs.Count; index += 2)
            {
                var original = pairs[index] as string ?? string.Empty;
                if (!seen.Add(original)) continue;
                var target = MapCopiedReferencePath(original, working, initial);
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

        foreach (var (owner, oldPath, newPath) in plan)
        {
            if (!replaceDependency(owner, oldPath, newPath))
                throw new InvalidOperationException($"SolidWorks 未能更新工作装配引用：{owner} -> {newPath}");
            log?.Invoke($"Working assembly reference rebound. Owner={owner}; Target={newPath}");
        }
        foreach (var owner in plan.Select(change => change.Owner).Distinct(StringComparer.OrdinalIgnoreCase))
            ValidateSavedPairs(owner, working, initial, Read(owner), File.Exists);

        log?.Invoke($"Working assembly references verified. Assemblies={visited.Count}; Rebound={plan.Count}");
        return plan.Count;
    }

    public static string MapCopiedReferencePath(string reference, string working, string initial)
    {
        if (!Path.IsPathFullyQualified(reference))
            throw new InvalidOperationException($"装配引用不是绝对路径：{reference}");
        var fullReference = Path.GetFullPath(reference);
        working = WorkspaceSafetyPolicy.Full(working);
        initial = WorkspaceSafetyPolicy.Full(initial);
        if (WorkspaceSafetyPolicy.Within(fullReference, working)) return fullReference;
        var fromInitial = WorkspaceSafetyPolicy.Within(fullReference, initial);
        var fromLegacyGate = WorkspaceSafetyPolicy.Within(fullReference, LegacyFixedGateRoot);
        if (!fromInitial && !fromLegacyGate && !WorkspaceSafetyPolicy.Within(fullReference, LegacyInspectionCarRoot))
            throw new InvalidOperationException($"装配引用既不在工作副本也不在初始模型目录：{fullReference}");
        var relative = fromLegacyGate
            ? Path.Combine("门架图", "固定门架", Path.GetRelativePath(LegacyFixedGateRoot, fullReference))
            : Path.GetRelativePath(fromInitial ? initial : LegacyInspectionCarRoot, fullReference);
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
