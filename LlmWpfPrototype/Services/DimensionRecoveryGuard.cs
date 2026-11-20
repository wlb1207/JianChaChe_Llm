using System.IO;
using System.Text.Json;

namespace LlmWpfPrototype.Services;

// A crash or an unverified recovery must block subsequent transactions, even after restart.
public sealed class DimensionRecoveryGuard(string initial, string working)
{
    public string MarkerPath => Path.Combine(WorkspaceSafetyPolicy.Full(working), ".dimension-transaction-pending.json");
    public void RequireClear()
    {
        WorkspaceSafetyPolicy.RejectLinks(working);
        if (File.Exists(MarkerPath))
            throw new InvalidOperationException($"状态未知：工作区存在尚未独立核验的尺寸事务，禁止继续修改。证据：{MarkerPath}");
    }
    public void Begin(IReadOnlyList<string> paths, object? snapshots = null)
    {
        RequireClear();
        if (paths.Count == 0) throw new InvalidOperationException("Empty transaction marker");
        foreach (var path in paths) WorkspaceSafetyPolicy.RequireWritableModel(path, initial, working);
        using var stream = new FileStream(MarkerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, new { StartedUtc = DateTimeOffset.UtcNow, Paths = paths, Snapshots = snapshots, State = "PendingIndependentVerification" });
        stream.Flush(true);
    }
    public void CompleteVerified()
    {
        WorkspaceSafetyPolicy.RejectLinks(MarkerPath);
        WorkspaceSafetyPolicy.RequireOwnedOrEmpty(initial, working);
        File.Delete(MarkerPath); // This transaction marker only; never a model or backup.
    }
}
