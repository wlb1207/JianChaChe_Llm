using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public interface ISolidWorksService
{
    bool IsSolidWorksRunning();

    bool HasActiveDocument();

    bool TryGetActiveDocumentInfoWithoutStartingSolidWorks(out string activeDocumentPath);

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
