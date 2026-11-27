using System.IO;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public enum SolidWorksSessionState
{
    NotRunning,
    ProcessRunningComUnavailable,
    ConnectedNoActiveDocument,
    ConnectedDifferentDocument,
    ConnectedWorkingAssembly
}

public sealed record SolidWorksSessionDiagnostics(
    SolidWorksSessionState State,
    bool HasRunningProcess,
    int RunningProcessCount,
    bool IsComAttached,
    string ActiveDocumentPath,
    string ActiveDocumentName,
    bool ActiveDocumentIsAssembly,
    bool HasOpenWorkingAssemblyInSession,
    string OpenWorkingAssemblyPath,
    string ExpectedWorkingFolder,
    int LastAttachHResult,
    string DetailMessage)
{
    public bool IsWorkingModelReady =>
        State == SolidWorksSessionState.ConnectedWorkingAssembly ||
        HasOpenWorkingAssemblyInSession;
}

public interface ISolidWorksService
{
    bool IsSolidWorksRunning();

    bool HasActiveDocument();

    bool TryGetActiveDocumentInfoWithoutStartingSolidWorks(out string activeDocumentPath);

    SolidWorksSessionDiagnostics GetSessionDiagnostics()
    {
        var hasActive = TryGetActiveDocumentInfoWithoutStartingSolidWorks(out var activePath);
        var running = hasActive || IsSolidWorksRunning();
        var fileName = string.IsNullOrWhiteSpace(activePath) ? string.Empty : Path.GetFileName(activePath);
        if (!running)
        {
            return new SolidWorksSessionDiagnostics(
                SolidWorksSessionState.NotRunning,
                HasRunningProcess: false,
                RunningProcessCount: 0,
                IsComAttached: false,
                ActiveDocumentPath: string.Empty,
                ActiveDocumentName: string.Empty,
                ActiveDocumentIsAssembly: false,
                HasOpenWorkingAssemblyInSession: false,
                OpenWorkingAssemblyPath: string.Empty,
                ExpectedWorkingFolder: string.Empty,
                LastAttachHResult: 0,
                DetailMessage: "当前未检测到运行中的 SolidWorks 进程。");
        }

        if (!hasActive)
        {
            return new SolidWorksSessionDiagnostics(
                SolidWorksSessionState.ConnectedNoActiveDocument,
                HasRunningProcess: true,
                RunningProcessCount: 1,
                IsComAttached: true,
                ActiveDocumentPath: string.Empty,
                ActiveDocumentName: string.Empty,
                ActiveDocumentIsAssembly: false,
                HasOpenWorkingAssemblyInSession: false,
                OpenWorkingAssemblyPath: string.Empty,
                ExpectedWorkingFolder: string.Empty,
                LastAttachHResult: 0,
                DetailMessage: "SolidWorks 已运行，但当前尚未打开工作模型文档。");
        }

        var isAssembly = activePath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
        return new SolidWorksSessionDiagnostics(
            SolidWorksSessionState.ConnectedWorkingAssembly,
            HasRunningProcess: true,
            RunningProcessCount: 1,
            IsComAttached: true,
            ActiveDocumentPath: activePath,
            ActiveDocumentName: fileName,
            ActiveDocumentIsAssembly: isAssembly,
            HasOpenWorkingAssemblyInSession: isAssembly,
            OpenWorkingAssemblyPath: isAssembly ? activePath : string.Empty,
            ExpectedWorkingFolder: string.Empty,
            LastAttachHResult: 0,
            DetailMessage: $"当前工作模型已打开：{fileName}。");
    }

    string GetLastOpenFailureMessage() => string.Empty;

    Task<bool> EnsureSolidWorksAsync(Action<string>? logWriter = null, CancellationToken cancellationToken = default);

    Task<bool> OpenInitialInspectionCarModelAsync(Action<string>? logWriter = null, CancellationToken cancellationToken = default);

    Task<bool> OpenAssemblyAsync(string assemblyPath, Action<string>? logWriter = null, CancellationToken cancellationToken = default);

    Task<bool> CloseDocumentsUnderFolderAsync(string folderPath, Action<string>? logWriter = null, CancellationToken cancellationToken = default);

    string GetActiveDocumentPath();

    Task<bool> UpdateDimensionsAsync(
        IReadOnlyList<SolidWorksDimensionUpdateRequest> updates,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default);

    Task<SolidWorksDimensionProbeResult> ProbeDimensionAsync(
        string partFilePath,
        string dimensionName,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default);

    Task<SolidWorksDimensionProbeResult> ReadOpenDimensionAsync(
        string partFilePath,
        string dimensionName,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SolidWorksDimensionScanItem>> ListPartDimensionsAsync(
        string partFilePath,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default);

    Task<bool> RebuildAssemblyAsync(string assemblyPath, Action<string>? logWriter = null, CancellationToken cancellationToken = default);

    Task<SolidWorksDimensionScanResult> ScanDimensionsAsync(
        string assemblyPath,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default);
}
