namespace LlmWpfPrototype.Services;

// Shared production transaction flow; offline tests substitute only the CAD operations.
internal sealed record DimensionTransactionChange(
    string Identity, string DocumentIdentity, Func<bool> Apply, Func<bool> Restore,
    Func<bool> VerifyTarget, Func<bool> VerifyOriginal);

internal sealed record DimensionTransactionDocument(
    string Identity, Func<bool> Preflight, Func<bool> Rebuild, Func<bool> Save)
{
    // A Save result and an in-memory read are not proof of persisted values.
    public Func<bool>? VerifyPersistedTarget { get; init; }
    public Func<bool>? VerifyPersistedOriginal { get; init; }
}

internal sealed record DimensionTransactionResult(
    bool Succeeded, string FailedStage, string Error, int AttemptedWrites,
    bool MemoryRestoreVerified, string DiskState, bool RequiresInspection)
{
    public string UserMessage => Succeeded ? (DiskState == "TargetVerifiedAfterReopen"
        ? "修改、重建、读回、保存及重开验证均已通过。" : "尺寸已重建、保存，并在当前总装配中读回核对。") :
        $"模型修改未通过。阶段={FailedStage}；原因={Error}；写入尝试={AttemptedWrites}；" +
        (AttemptedWrites == 0 ? "本事务零尺寸写入；" : $"内存恢复={(MemoryRestoreVerified ? "已验证" : "状态未知")}；") +
        $"磁盘状态={DiskState}；" + (RequiresInspection ? "已停止本次修改，请核验工作模型后再试。" : "未报告修改成功。");
}

internal static class DimensionTransaction
{
    internal static bool Execute(
        IReadOnlyList<DimensionTransactionChange> changes,
        IReadOnlyList<DimensionTransactionDocument> documents,
        Action<string> log, CancellationToken cancellationToken)
        => ExecuteDetailed(changes, documents, log, cancellationToken).Succeeded;

    internal static DimensionTransactionResult ExecuteDetailed(
        IReadOnlyList<DimensionTransactionChange> changes,
        IReadOnlyList<DimensionTransactionDocument> documents,
        Action<string> log, CancellationToken cancellationToken)
        => ExecuteCore(changes, documents, log, cancellationToken, requireReopen: true);

    internal static DimensionTransactionResult ExecuteInSession(
        IReadOnlyList<DimensionTransactionChange> changes,
        IReadOnlyList<DimensionTransactionDocument> documents,
        Action<string> log, CancellationToken cancellationToken)
        => ExecuteCore(changes, documents, log, cancellationToken, requireReopen: false);

    private static DimensionTransactionResult ExecuteCore(
        IReadOnlyList<DimensionTransactionChange> changes,
        IReadOnlyList<DimensionTransactionDocument> documents,
        Action<string> log, CancellationToken cancellationToken, bool requireReopen)
    {
        var attempted = new List<DimensionTransactionChange>();
        var saveAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stage = "Preflight";
        try
        {
            Require(changes.Count > 0 && documents.Count > 0, "Empty transaction");
            Require(changes.Select(x => x.Identity).Distinct(StringComparer.OrdinalIgnoreCase).Count() == changes.Count,
                "Duplicate dimension identity in plan");
            Require(documents.Select(x => x.Identity).Distinct(StringComparer.OrdinalIgnoreCase).Count() == documents.Count,
                "Duplicate document identity in plan");
            Require(changes.All(change => documents.Any(doc => string.Equals(doc.Identity, change.DocumentIdentity,
                StringComparison.OrdinalIgnoreCase))), "Dimension has no document preflight");
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(document.Preflight(), $"Preflight failed: {document.Identity}");
            }
            log($"Transaction preflight passed. Dimensions={changes.Count}, Documents={documents.Count}");
            stage = "Write";
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Even a failed setter may have changed the value.
                attempted.Add(change);
                Require(change.Apply(), $"Write failed: {change.Identity}");
            }
            stage = "Rebuild";
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(document.Rebuild(), $"Rebuild failed: {document.Identity}");
            }
            stage = "ReadbackBeforeSave";
            foreach (var change in changes)
                Require(change.VerifyTarget(), $"Readback failed before save: {change.Identity}");
            stage = "Save";
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                saveAttempted.Add(document.Identity);
                log($"Transaction save attempt. Path={document.Identity}; MultiFileAtomic=False");
                Require(document.Save(), $"Save failed: {document.Identity}");
            }
            stage = "ReadbackAfterSave";
            foreach (var change in changes)
                Require(change.VerifyTarget(), $"Readback failed after save: {change.Identity}");
            if (!requireReopen)
            {
                log($"Transactional dimension update completed. Count={changes.Count}, InSessionReadback=True");
                return new(true, "", "", attempted.Count, false, "SavedAndReadBackInSession", false);
            }
            stage = "ReopenVerification";
            if (documents.Any(document => document.VerifyPersistedTarget is null))
            {
                log("Persisted target verification unavailable. Saved values are NOT independently verified; further writes blocked.");
                return new(false, stage, "缺少隔离副本重开读回证据；保存返回成功不能替代此证据。", attempted.Count,
                    false, "SavedButNotReopened", true);
            }
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(document.VerifyPersistedTarget!(), $"Persisted readback failed: {document.Identity}");
            }
            log($"Transactional dimension update completed. Count={changes.Count}, ReopenVerified=True");
            return new(true, "", "", attempted.Count, false, "TargetVerifiedAfterReopen", false);
        }
        catch (Exception ex) when (ex is not AccessViolationException)
        {
            log($"Transaction failed. Stage={stage}, Error={ex.Message}, HResult=0x{ex.HResult:X8}, AttemptedWrites={attempted.Count}, SaveAttempts={saveAttempted.Count}");
            if (attempted.Count == 0)
            {
                log("Transaction rejected before writes. DimensionWriteCount=0");
                return new(false, stage, ex.Message, 0, true, "NotWrittenByThisTransaction", false);
            }
            if (IsDisconnected(ex))
                return new(false, stage, ex.Message, attempted.Count, false,
                    saveAttempted.Count == 0 ? "NotWrittenByThisTransaction" : "UnknownPartialSave", true);
            // Every independent planned dimension was snapshotted before the first write.
            // A setter/rebuild may indirectly change a later target that was not attempted yet.
            var restored = Rollback(changes, documents, saveAttempted, log);
            return new(false, stage, ex.Message, attempted.Count, restored.MemoryVerified,
                restored.DiskState, !restored.Verified);
        }
    }

    private static bool IsDisconnected(Exception ex) => ex.HResult is
        unchecked((int)0x800706BE) or unchecked((int)0x800706BA) or unchecked((int)0x80010108);

    private static (bool Verified, bool MemoryVerified, string DiskState) Rollback(IReadOnlyList<DimensionTransactionChange> planned,
        IReadOnlyList<DimensionTransactionDocument> documents, ISet<string> saveAttempted, Action<string> log)
    {
        var verified = true;
        var disconnected = false;
        bool Check(Func<bool> action, string stage, string identity)
        {
            if (disconnected) return false;
            try
            {
                var result = action();
                log($"Rollback {stage}. Target={identity}, Succeeded={result}");
                return result;
            }
            catch (Exception ex) when (ex is not AccessViolationException)
            {
                disconnected = IsDisconnected(ex);
                log($"Rollback {stage} failed. Target={identity}, Error={ex.Message}, HResult=0x{ex.HResult:X8}");
                return false;
            }
        }
        // Restore every preflighted dimension. A CAD setter can change a linked
        // dimension that has not reached its own Apply delegate yet.
        foreach (var change in planned.Reverse())
            verified &= Check(change.Restore, "write", change.Identity);
        var affected = documents.ToList();
        foreach (var document in affected)
            verified &= Check(document.Rebuild, "rebuild", document.Identity);
        foreach (var change in planned)
            verified &= Check(change.VerifyOriginal, "readback", change.Identity);
        var memoryVerified = verified;
        var diskState = saveAttempted.Count == 0 ? "NotWrittenByThisTransaction" : "UnknownPartialSave";
        var diskVerified = saveAttempted.Count == 0;
        if (saveAttempted.Count > 0 && memoryVerified)
        {
            var persisted = affected.Where(document => saveAttempted.Contains(document.Identity)).ToList();
            foreach (var document in persisted)
                verified &= Check(document.Save, "save restored values", document.Identity);
            foreach (var change in planned)
                verified &= Check(change.VerifyOriginal, "readback after rollback save", change.Identity);
            if (verified)
            {
                diskState = "RestoreSaveAcknowledgedNotReopened";
                diskVerified = persisted.All(document => document.VerifyPersistedOriginal is not null);
                if (diskVerified)
                    foreach (var document in persisted)
                        diskVerified &= Check(document.VerifyPersistedOriginal!, "persisted original after reopen", document.Identity);
                if (diskVerified) diskState = "OriginalValuesVerifiedAfterReopen";
            }
        }
        verified &= diskVerified;
        log($"Rollback completed. Verified={verified}, MemoryVerified={memoryVerified}, DiskState={diskState}, SaveAttempts={saveAttempted.Count}, ManualInspectionRequired={!verified}");
        return (verified, memoryVerified, diskState);
    }

    private static void Require(bool succeeded, string message)
    {
        if (!succeeded) throw new InvalidOperationException(message);
    }
}
