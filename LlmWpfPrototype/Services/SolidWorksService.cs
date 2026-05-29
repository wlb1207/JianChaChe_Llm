using Microsoft.VisualBasic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Forms = System.Windows.Forms;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class SolidWorksService : ISolidWorksService
{
    private const string ProgId = "SldWorks.Application";
    private const string InitialInspectionCarAssemblyPath =
        @"E:\反力架\反力架\三六重工v2\不伸缩单层检查车\Solidworks模型\初始模型\总装配\总装配体.SLDASM";

    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions ScanJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly object _syncRoot = new();
    private readonly string _initialModelFolder;
    private readonly string _workingModelFolder;
    private SldWorks? _swApp;

    private sealed record PreparedDimensionUpdate(
        SolidWorksDimensionUpdateRequest Request,
        IModelDoc2 Model,
        IDimension Dimension,
        double TargetSystemValue,
        double OriginalSystemValue,
        int ConfigurationOption,
        object? ConfigurationNames,
        string ResolvedDimensionName,
        string ResolutionMode,
        bool WasOpenedByThisFlow);

    private sealed record DimensionUpdateTransactionContext(
        string OriginalActiveDocumentTitle,
        string OriginalActiveDocumentPath,
        string AssemblyDocumentTitle,
        string AssemblyDocumentPath,
        bool StartedFromAssembly);

    private sealed record ResolvedDimensionReference(
        IDimension Dimension,
        string FullDimensionName,
        string FeatureName,
        string SketchName,
        decimal? CurrentValue,
        string ResolutionMode);

    public SolidWorksService(SolidWorksWorkspaceOptions? workspaceOptions = null)
    {
        _initialModelFolder = string.IsNullOrWhiteSpace(workspaceOptions?.InitialModelFolder)
            ? string.Empty
            : NormalizeDirectoryPath(workspaceOptions.InitialModelFolder);
        _workingModelFolder = string.IsNullOrWhiteSpace(workspaceOptions?.WorkingModelFolder)
            ? string.Empty
            : NormalizeDirectoryPath(workspaceOptions.WorkingModelFolder);
    }

    public bool IsSolidWorksRunning()
    {
        try
        {
            return TryGetOrCreateApplication(allowCreate: false, logWriter: null, out var application) && application is not null;
        }
        catch
        {
            return false;
        }
    }

    public bool HasActiveDocument()
    {
        try
        {
            return TryGetActiveDocumentInfoWithoutStartingSolidWorks(out _);
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetActiveDocumentInfoWithoutStartingSolidWorks(out string activeDocumentPath)
    {
        activeDocumentPath = string.Empty;

        try
        {
            if (!TryGetRunningApplication(out var application) || application is null)
            {
                return false;
            }

            var activeDocument = application.ActiveDoc as IModelDoc2;
            if (activeDocument is null)
            {
                return false;
            }

            activeDocumentPath = NormalizeFilePath(activeDocument.GetPathName() ?? string.Empty);
            return !string.IsNullOrWhiteSpace(activeDocumentPath);
        }
        catch
        {
            activeDocumentPath = string.Empty;
            return false;
        }
    }

    public string GetActiveDocumentPath()
    {
        try
        {
            return TryGetActiveDocumentInfoWithoutStartingSolidWorks(out var activeDocumentPath)
                ? activeDocumentPath
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public Task<bool> EnsureSolidWorksAsync(Action<string>? logWriter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var success = TryGetOrCreateApplication(allowCreate: true, logWriter, out _);
            return Task.FromResult(success);
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "确保 SolidWorks 可用时发生异常。", ex);
            return Task.FromResult(false);
        }
    }

    public async Task<bool> OpenInitialInspectionCarModelAsync(Action<string>? logWriter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var assemblyPath = ResolveInitialInspectionCarAssemblyPath(logWriter);
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            WriteLog(logWriter, "未找到初始检查车模型文件。");
            return false;
        }

        return await OpenAssemblyAsync(assemblyPath, logWriter, cancellationToken);
    }

    public async Task<bool> OpenAssemblyAsync(string assemblyPath, Action<string>? logWriter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!ValidateExistingFile(assemblyPath, logWriter, "装配体"))
            {
                return false;
            }

            WriteLog(logWriter, $"请求打开路径：{assemblyPath}");

            if (!await EnsureSolidWorksAsync(logWriter, cancellationToken))
            {
                WriteLog(logWriter, "启动或连接 SolidWorks 失败。");
                return false;
            }

            await WaitForSolidWorksStartupAsync(logWriter, cancellationToken);

            if (TryActivateOpenedDocument(assemblyPath, logWriter))
            {
                WriteLog(logWriter, "目标装配体已经打开，已切换到对应文档。");
                return true;
            }

            if (TryOpenDocumentByCom(assemblyPath, (int)swDocumentTypes_e.swDocASSEMBLY, logWriter) &&
                TryActivateOpenedDocument(assemblyPath, logWriter))
            {
                WriteLog(logWriter, $"已通过 COM 打开装配体：{assemblyPath}");
                return true;
            }

            if (!await TryOpenDocumentByUiAutomationAsync(assemblyPath, logWriter, cancellationToken))
            {
                return false;
            }

            if (TryActivateOpenedDocument(assemblyPath, logWriter))
            {
                WriteLog(logWriter, $"已通过界面自动化确认装配体打开成功：{assemblyPath}");
                return true;
            }

            WriteLog(logWriter, $"已执行打开操作，但尚未确认目标装配体已加载：{assemblyPath}");
            return false;
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "打开 SolidWorks 装配体失败。", ex);
            return false;
        }
    }

    public Task<bool> CloseDocumentsUnderFolderAsync(
        string folderPath,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                WriteLog(logWriter, "未提供需要关闭文档的工作区目录。");
                return Task.FromResult(false);
            }

            if (!TryGetOrCreateApplication(allowCreate: false, logWriter, out var application) || application is null)
            {
                WriteLog(logWriter, "当前没有运行中的 SolidWorks 实例，无需关闭工作区文档。");
                return Task.FromResult(true);
            }

            var normalizedFolder = NormalizeDirectoryPath(folderPath);
            WriteLog(logWriter, "正在关闭旧工作模型文档");
            var documentsToClose = GetOpenDocuments(application)
                .Where(document =>
                    IsPathUnderFolder(document.GetPathName(), normalizedFolder) ||
                    IsAssemblyReferencingFolder(document, normalizedFolder))
                .DistinctBy(document => NormalizeFilePath(document.GetPathName()))
                .OrderByDescending(GetDocumentClosePriority)
                .ToList();

            if (documentsToClose.Count == 0)
            {
                WriteLog(logWriter, $"未检测到位于工作区目录下的已打开文档：{normalizedFolder}");
                return Task.FromResult(true);
            }

            foreach (var document in documentsToClose)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var title = document.GetTitle();
                var path = document.GetPathName();

                lock (_syncRoot)
                {
                    application.CloseDoc(title);
                }

                WriteLog(logWriter, $"已关闭文档：{NormalizeFilePath(path)}");
            }

            var hasRemainingDocuments = GetOpenDocuments(application)
                .Any(document =>
                    IsPathUnderFolder(document.GetPathName(), normalizedFolder) ||
                    IsAssemblyReferencingFolder(document, normalizedFolder));

            if (hasRemainingDocuments)
            {
                WriteLog(logWriter, $"仍有工作区文档未关闭：{normalizedFolder}");
                return Task.FromResult(false);
            }

            WriteLog(logWriter, $"已关闭工作区目录下的所有文档：{normalizedFolder}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "关闭工作区目录下的 SolidWorks 文档失败。", ex);
            return Task.FromResult(false);
        }
    }

    public async Task<bool> UpdateDimensionsAsync(
        IReadOnlyList<SolidWorksDimensionUpdateRequest> updates,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (updates.Count == 0)
        {
            WriteLog(logWriter, "没有可执行的 SolidWorks 尺寸更新请求。");
            return false;
        }

        if (!await EnsureSolidWorksAsync(logWriter, cancellationToken))
        {
            return false;
        }

        await WaitForSolidWorksStartupAsync(logWriter, cancellationToken);
        return TryApplyDimensionUpdatesTransactionally(updates, logWriter, cancellationToken);
    }

    public async Task<SolidWorksDimensionProbeResult> ProbeDimensionAsync(
        string partFilePath,
        string dimensionName,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new SolidWorksDimensionProbeResult
        {
            PartFilePath = partFilePath,
            RequestedDimensionName = dimensionName
        };

        if (!ValidateExistingFile(partFilePath, logWriter, "零件"))
        {
            result.Message = "零件文件不存在。";
            return result;
        }

        if (!await EnsureSolidWorksAsync(logWriter, cancellationToken))
        {
            result.Message = "无法连接或启动 SolidWorks。";
            return result;
        }

        await WaitForSolidWorksStartupAsync(logWriter, cancellationToken);

        if (!TryResolveDimensionReferenceForPart(partFilePath, dimensionName, logWriter, out var reference))
        {
            result.Message = "未找到对应尺寸。";
            WriteLog(logWriter, $"开始枚举零件中的可用尺寸：{partFilePath}");
            WriteDimensionEnumerationDiagnostics(partFilePath, logWriter);
            return result;
        }

        result.Found = true;
        result.ResolvedDimensionName = string.IsNullOrWhiteSpace(reference.FullDimensionName)
            ? dimensionName
            : reference.FullDimensionName;
        result.FeatureName = reference.FeatureName;
        result.SketchName = reference.SketchName;
        result.CurrentValue = reference.CurrentValue;
        result.Message = "已找到对应尺寸。";
        return result;
    }

    public async Task<IReadOnlyList<SolidWorksDimensionScanItem>> ListPartDimensionsAsync(
        string partFilePath,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ValidateExistingFile(partFilePath, logWriter, "零件"))
        {
            return [];
        }

        if (!await EnsureSolidWorksAsync(logWriter, cancellationToken))
        {
            return [];
        }

        await WaitForSolidWorksStartupAsync(logWriter, cancellationToken);
        var result = new SolidWorksDimensionScanResult();

        try
        {
            var model = GetOpenedDocumentByPath(partFilePath);
            if (model is null && !TryOpenDocumentByCom(partFilePath, (int)swDocumentTypes_e.swDocPART, logWriter))
            {
                return [];
            }

            model = GetOpenedDocumentByPath(partFilePath);
            if (model is null)
            {
                return [];
            }

            ActivateDocument(model, logWriter);
            ScanModelDocument(Path.GetFileNameWithoutExtension(partFilePath), partFilePath, model, result, logWriter);
            return result.Items;
        }
        catch (Exception ex)
        {
            WriteException(logWriter, $"枚举零件尺寸失败：{partFilePath}。", ex);
            return [];
        }
    }

    public async Task<bool> RebuildAssemblyAsync(string assemblyPath, Action<string>? logWriter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!await OpenAssemblyAsync(assemblyPath, logWriter, cancellationToken))
            {
                return false;
            }

            lock (_syncRoot)
            {
                var assemblyDocument = GetOpenedDocumentByPath(assemblyPath);
                if (assemblyDocument is null)
                {
                    WriteLog(logWriter, $"未找到需要重建的装配体文档：{assemblyPath}");
                    return false;
                }

                var rebuildSucceeded = assemblyDocument.ForceRebuild3(false);
                var saveSucceeded = SaveDocument(assemblyDocument, logWriter);
                WriteLog(logWriter, $"装配体重建结果：Rebuild={rebuildSucceeded}，Save={saveSucceeded}，Path={assemblyPath}");
                return rebuildSucceeded && saveSucceeded;
            }
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "重建 SolidWorks 装配体失败。", ex);
            return false;
        }
    }

    public async Task<SolidWorksDimensionScanResult> ScanDimensionsAsync(
        string assemblyPath,
        Action<string>? logWriter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new SolidWorksDimensionScanResult();
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(outputDirectory);
        result.JsonOutputPath = Path.Combine(outputDirectory, "dimension-scan-result.json");
        result.CsvOutputPath = Path.Combine(outputDirectory, "dimension-scan-result.csv");
        var temporaryOpenedDocumentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!await EnsureSolidWorksAsync(logWriter, cancellationToken))
            {
                result.Errors.Add("无法连接或启动 SolidWorks。");
                WriteScanOutputs(result);
                return result;
            }

            lock (_syncRoot)
            {
                var activeDocument = GetActiveDocument();
                if (activeDocument is not null)
                {
                    var activePath = activeDocument.GetPathName() ?? string.Empty;
                    var activeName = Path.GetFileNameWithoutExtension(activePath);
                    WriteLog(logWriter, $"扫描当前已打开文档：{activeDocument.GetTitle()} ({activePath})");
                    ScanDocumentAndChildren(
                        activeName,
                        activePath,
                        activeDocument,
                        result,
                        logWriter,
                        temporaryOpenedDocumentPaths);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(assemblyPath))
                    {
                        result.Errors.Add("当前没有已打开模型，且未提供可扫描的装配体路径。");
                    }
                    else
                    {
                        var openedDocument = OpenDocumentReadOnlyForScan(assemblyPath, logWriter, temporaryOpenedDocumentPaths);
                        if (openedDocument is null)
                        {
                            result.Errors.Add($"无法以只读方式打开装配体：{assemblyPath}");
                        }
                        else
                        {
                            ScanDocumentAndChildren(
                                Path.GetFileNameWithoutExtension(assemblyPath),
                                assemblyPath,
                                openedDocument,
                                result,
                                logWriter,
                                temporaryOpenedDocumentPaths);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"扫描 SolidWorks 尺寸失败：{ex.Message}");
            WriteException(logWriter, "扫描 SolidWorks 尺寸失败。", ex);
        }
        finally
        {
            CloseTemporaryDocuments(temporaryOpenedDocumentPaths, logWriter);
        }

        WriteScanOutputs(result);
        WriteLog(logWriter, $"尺寸扫描完成：JSON={result.JsonOutputPath}，CSV={result.CsvOutputPath}，Items={result.Items.Count}，Errors={result.Errors.Count}");
        return result;
    }

    private bool TryApplyDimensionUpdate(SolidWorksDimensionUpdateRequest update, Action<string>? logWriter)
    {
        try
        {
            if (!ValidateExistingFile(update.PartFilePath, logWriter, "零件"))
            {
                return false;
            }

            if (!TryParseNumericValue(update.Value, out var numericValue))
            {
                WriteLog(logWriter, $"参数 {update.ParameterName} 的值无法解析为数字：{update.Value}");
                return false;
            }

            var model = GetOpenedDocumentByPath(update.PartFilePath);
            if (model is null && !TryOpenDocumentByCom(update.PartFilePath, (int)swDocumentTypes_e.swDocPART, logWriter))
            {
                return false;
            }

            model = GetOpenedDocumentByPath(update.PartFilePath);
            if (model is null)
            {
                WriteLog(logWriter, $"打开零件后仍未获取到文档对象：{update.PartFilePath}");
                return false;
            }

            ActivateDocument(model, logWriter);

            if (!TryResolveDimensionReference(model, update.DimensionName, out var reference))
            {
                WriteLog(logWriter, $"未找到尺寸参数：{update.DimensionName}，零件：{update.PartFilePath}");
                return false;
            }

            var systemValue = ConvertToSystemValue(numericValue, update.Unit);
            var configurationOption = string.IsNullOrWhiteSpace(update.Configuration)
                ? (int)swInConfigurationOpts_e.swThisConfiguration
                : (int)swInConfigurationOpts_e.swSpecifyConfiguration;
            object? configurationNames = string.IsNullOrWhiteSpace(update.Configuration)
                ? null
                : new[] { update.Configuration };

            var setValueStatus = reference.Dimension.SetSystemValue3(systemValue, configurationOption, configurationNames);
            var updateSucceeded = setValueStatus == (int)swSetValueReturnStatus_e.swSetValue_Successful;
            var rebuildSucceeded = model.EditRebuild3();
            var saveSucceeded = SaveDocument(model, logWriter);

            WriteLog(
                logWriter,
                $"尺寸更新：参数={update.ParameterName}，零件={update.PartFilePath}，尺寸={update.DimensionName}，值={numericValue.ToString(CultureInfo.InvariantCulture)} {NormalizeUnit(update.Unit)}，Update={updateSucceeded}，Rebuild={rebuildSucceeded}，Save={saveSucceeded}");

            return updateSucceeded && rebuildSucceeded && saveSucceeded;
        }
        catch (Exception ex)
        {
            WriteException(logWriter, $"更新 SolidWorks 尺寸失败：{update.ParameterName}", ex);
            return false;
        }
    }

    private bool TryApplyDimensionUpdatesTransactionally(
        IReadOnlyList<SolidWorksDimensionUpdateRequest> updates,
        Action<string>? logWriter,
        CancellationToken cancellationToken)
    {
        var preparedUpdates = new List<PreparedDimensionUpdate>();
        var touchedDocuments = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
        var transactionContext = CaptureTransactionContext(logWriter);

        try
        {
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryPrepareDimensionUpdate(update, logWriter, out var preparedUpdate))
                {
                    WriteLog(logWriter, $"Transaction cancelled during validation. Parameter={update.ParameterName}");
                    RestorePreparedUpdates(preparedUpdates, logWriter);
                    return false;
                }

                preparedUpdates.Add(preparedUpdate);
                touchedDocuments[NormalizeFilePath(preparedUpdate.Model.GetPathName())] = preparedUpdate.Model;
            }

            foreach (var preparedUpdate in preparedUpdates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var setValueStatus = ApplyPreparedDimensionValue(preparedUpdate);
                if (setValueStatus != (int)swSetValueReturnStatus_e.swSetValue_Successful)
                {
                    WriteLog(
                        logWriter,
                        $"Transaction write failed. Parameter={preparedUpdate.Request.ParameterName}, Dimension={preparedUpdate.Request.DimensionName}, Status={setValueStatus}");
                    RestorePreparedUpdates(preparedUpdates, logWriter);
                    return false;
                }
            }

            foreach (var document in touchedDocuments.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!document.ForceRebuild3(false))
                {
                    WriteLog(logWriter, $"Transaction rebuild failed. Path={document.GetPathName()}");
                    RestorePreparedUpdates(preparedUpdates, logWriter);
                    return false;
                }
            }

            foreach (var document in touchedDocuments.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!SaveDocument(document, logWriter))
                {
                    WriteLog(logWriter, $"Transaction save failed. Path={document.GetPathName()}");
                    RestorePreparedUpdates(preparedUpdates, logWriter);
                    return false;
                }
            }

            RestoreAssemblyViewAfterDimensionUpdates(transactionContext, preparedUpdates, logWriter);

            foreach (var preparedUpdate in preparedUpdates)
            {
                decimal? updatedValue = null;
                if (TryReadDimensionCurrentValue((dynamic)preparedUpdate.Dimension, out double valueInMillimeters))
                {
                    updatedValue = Convert.ToDecimal(valueInMillimeters, CultureInfo.InvariantCulture);
                }

                WriteLog(
                    logWriter,
                    $"Updated dimension. Parameter={preparedUpdate.Request.ParameterName}, Part={preparedUpdate.Request.PartFilePath}, RequestedDimension={preparedUpdate.Request.DimensionName}, ResolvedDimension={preparedUpdate.ResolvedDimensionName}, Mode={preparedUpdate.ResolutionMode}, Before={Convert.ToDecimal(preparedUpdate.OriginalSystemValue * 1000d, CultureInfo.InvariantCulture)} mm, After={updatedValue} mm, NewValue={preparedUpdate.Request.Value} {NormalizeUnit(preparedUpdate.Request.Unit)}");
            }

            WriteLog(logWriter, $"Transactional dimension update completed. Count={preparedUpdates.Count}");
            return true;
        }
        catch (OperationCanceledException)
        {
            RestorePreparedUpdates(preparedUpdates, logWriter);
            throw;
        }
        catch (Exception ex)
        {
            RestorePreparedUpdates(preparedUpdates, logWriter);
            WriteException(logWriter, "Transactional SolidWorks dimension update failed. ", ex);
            return false;
        }
    }

    private bool TryPrepareDimensionUpdate(
        SolidWorksDimensionUpdateRequest update,
        Action<string>? logWriter,
        out PreparedDimensionUpdate preparedUpdate)
    {
        preparedUpdate = null!;

        if (!ValidateExistingFile(update.PartFilePath, logWriter, "零件"))
        {
            return false;
        }

        if (!TryParseNumericValue(update.Value, out var numericValue))
        {
            WriteLog(logWriter, $"Invalid numeric value. Parameter={update.ParameterName}, Value={update.Value}");
            return false;
        }

        var model = GetOpenedDocumentByPath(update.PartFilePath);
        var wasOpenedByThisFlow = false;
        if (model is null)
        {
            if (!TryOpenDocumentByCom(update.PartFilePath, (int)swDocumentTypes_e.swDocPART, logWriter))
            {
                return false;
            }

            wasOpenedByThisFlow = true;
        }

        model = GetOpenedDocumentByPath(update.PartFilePath);
        if (model is null)
        {
            return false;
        }

        ActivateDocument(model, logWriter);

        if (!TryResolveDimensionReference(model, update.DimensionName, out var reference))
        {
            WriteLog(logWriter, $"Dimension not found. Part={update.PartFilePath}, Dimension={update.DimensionName}");
            WriteLog(logWriter, $"Dimension lookup failed. Part={update.PartFilePath}, ModelTitle={model.GetTitle()}, RequestedDimension={update.DimensionName}");
            WriteDimensionEnumerationDiagnostics(update.PartFilePath, logWriter);
            return false;
        }

        var targetSystemValue = ConvertToSystemValue(numericValue, update.Unit);
        var configurationOption = string.IsNullOrWhiteSpace(update.Configuration)
            ? (int)swInConfigurationOpts_e.swThisConfiguration
            : (int)swInConfigurationOpts_e.swSpecifyConfiguration;
        object? configurationNames = string.IsNullOrWhiteSpace(update.Configuration)
            ? null
            : new[] { update.Configuration };

        preparedUpdate = new PreparedDimensionUpdate(
            update,
            model,
            reference.Dimension,
            targetSystemValue,
            reference.Dimension.SystemValue,
            configurationOption,
            configurationNames,
            reference.FullDimensionName,
            reference.ResolutionMode,
            wasOpenedByThisFlow);

        WriteLog(
            logWriter,
            $"Dimension resolved. Requested={update.DimensionName}, Resolved={reference.FullDimensionName}, Mode={reference.ResolutionMode}, Before={reference.CurrentValue} mm");

        return true;
    }

    private static int ApplyPreparedDimensionValue(PreparedDimensionUpdate preparedUpdate)
    {
        return preparedUpdate.Dimension.SetSystemValue3(
            preparedUpdate.TargetSystemValue,
            preparedUpdate.ConfigurationOption,
            preparedUpdate.ConfigurationNames);
    }

    private void RestorePreparedUpdates(
        IReadOnlyList<PreparedDimensionUpdate> preparedUpdates,
        Action<string>? logWriter)
    {
        foreach (var preparedUpdate in preparedUpdates.Reverse())
        {
            try
            {
                preparedUpdate.Dimension.SetSystemValue3(
                    preparedUpdate.OriginalSystemValue,
                    preparedUpdate.ConfigurationOption,
                    preparedUpdate.ConfigurationNames);
                preparedUpdate.Model.ForceRebuild3(false);
            }
            catch (Exception ex)
            {
                WriteException(logWriter, $"Rollback failed for dimension {preparedUpdate.Request.DimensionName}. ", ex);
            }
        }
    }

    private DimensionUpdateTransactionContext CaptureTransactionContext(Action<string>? logWriter)
    {
        lock (_syncRoot)
        {
            var activeDocument = _swApp?.ActiveDoc as IModelDoc2;
            var originalTitle = activeDocument?.GetTitle() ?? string.Empty;
            var originalPath = NormalizeFilePath(activeDocument?.GetPathName() ?? string.Empty);
            var originalType = activeDocument?.GetType();
            var startedFromAssembly = originalType == (int)swDocumentTypes_e.swDocASSEMBLY;

            var assemblyTitle = startedFromAssembly ? originalTitle : string.Empty;
            var assemblyPath = startedFromAssembly ? originalPath : string.Empty;

            WriteLog(logWriter, $"OriginalActiveDocument={originalTitle}");
            WriteLog(logWriter, $"AssemblyDocumentBeforeEdit={(string.IsNullOrWhiteSpace(assemblyTitle) ? "n/a" : assemblyTitle)}");

            return new DimensionUpdateTransactionContext(
                originalTitle,
                originalPath,
                assemblyTitle,
                assemblyPath,
                startedFromAssembly);
        }
    }

    private void RestoreAssemblyViewAfterDimensionUpdates(
        DimensionUpdateTransactionContext context,
        IReadOnlyList<PreparedDimensionUpdate> preparedUpdates,
        Action<string>? logWriter)
    {
        if (preparedUpdates.Count == 0)
        {
            return;
        }

        var editedPart = preparedUpdates.LastOrDefault();
        if (editedPart is null)
        {
            return;
        }

        var editedPartTitle = editedPart.Model.GetTitle();
        var editedPartPath = NormalizeFilePath(editedPart.Model.GetPathName() ?? string.Empty);
        WriteLog(logWriter, $"EditedPartDocument={editedPartTitle}");
        WriteLog(logWriter, "DimensionUpdateSucceeded=True");
        WriteLog(logWriter, "SavingEditedPart=True");

        var willCloseEditedPart = context.StartedFromAssembly &&
                                  !string.IsNullOrWhiteSpace(editedPartTitle) &&
                                  !string.Equals(editedPartTitle, context.OriginalActiveDocumentTitle, StringComparison.OrdinalIgnoreCase) &&
                                  (editedPart.WasOpenedByThisFlow || !string.Equals(editedPartPath, context.AssemblyDocumentPath, StringComparison.OrdinalIgnoreCase));
        WriteLog(logWriter, $"WillCloseEditedPartAfterSave={willCloseEditedPart}");

        if (willCloseEditedPart)
        {
            var closeSucceeded = TryCloseDocumentByTitle(editedPartTitle, logWriter);
            WriteLog(logWriter, $"CloseEditedPartResult={closeSucceeded}");
        }
        else
        {
            WriteLog(logWriter, "CloseEditedPartResult=Skipped");
        }

        if (!context.StartedFromAssembly)
        {
            WriteLog(logWriter, "ReactivatedAssemblyResult=Skipped");
            return;
        }

        var assemblyDocument = ResolveDocumentForReactivation(context);
        if (assemblyDocument is null)
        {
            WriteLog(logWriter, "ReactivatedAssemblyResult=False");
            return;
        }

        var reactivated = TryActivateDocumentByTitle(assemblyDocument.GetTitle(), logWriter);
        WriteLog(logWriter, $"ReactivatedAssembly={(reactivated ? assemblyDocument.GetTitle() : "False")}");
        if (reactivated)
        {
            var rebuildSucceeded = assemblyDocument.ForceRebuild3(false);
            WriteLog(logWriter, $"RebuildAssemblyAfterPartClose={rebuildSucceeded}");
            try
            {
                assemblyDocument.ViewZoomtofit2();
            }
            catch (Exception ex)
            {
                WriteException(logWriter, "Zoom to fit assembly after part close failed. ", ex);
            }
        }
        else
        {
            WriteLog(logWriter, "RebuildAssemblyAfterPartClose=False");
        }
    }

    private IModelDoc2? ResolveDocumentForReactivation(DimensionUpdateTransactionContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.AssemblyDocumentPath))
        {
            var byPath = GetOpenedDocumentByPath(context.AssemblyDocumentPath);
            if (byPath is not null)
            {
                return byPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(context.OriginalActiveDocumentPath))
        {
            var byOriginalPath = GetOpenedDocumentByPath(context.OriginalActiveDocumentPath);
            if (byOriginalPath is not null)
            {
                return byOriginalPath;
            }
        }

        return null;
    }

    private bool TryCloseDocumentByTitle(string documentTitle, Action<string>? logWriter)
    {
        try
        {
            lock (_syncRoot)
            {
                if (_swApp is null || string.IsNullOrWhiteSpace(documentTitle))
                {
                    return false;
                }

                _swApp.CloseDoc(documentTitle);
                return true;
            }
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "Close edited part failed. ", ex);
            return false;
        }
    }

    private bool TryActivateDocumentByTitle(string documentTitle, Action<string>? logWriter)
    {
        try
        {
            lock (_syncRoot)
            {
                if (_swApp is null || string.IsNullOrWhiteSpace(documentTitle))
                {
                    return false;
                }

                var errors = 0;
                _swApp.ActivateDoc3(
                    documentTitle,
                    false,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                    ref errors);
                return errors == 0;
            }
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "Reactivate assembly failed. ", ex);
            return false;
        }
    }

    private bool TryResolveDimensionReferenceForPart(
        string partFilePath,
        string dimensionName,
        Action<string>? logWriter,
        out ResolvedDimensionReference reference)
    {
        reference = null!;

        var model = GetOpenedDocumentByPath(partFilePath);
        if (model is null && !TryOpenDocumentByCom(partFilePath, (int)swDocumentTypes_e.swDocPART, logWriter))
        {
            return false;
        }

        model = GetOpenedDocumentByPath(partFilePath);
        if (model is null)
        {
            WriteLog(logWriter, $"Document object not available after opening part. Path={partFilePath}");
            return false;
        }

        ActivateDocument(model, logWriter);
        return TryResolveDimensionReference(model, dimensionName, out reference);
    }

    private static bool TryResolveDimensionReference(
        IModelDoc2 model,
        string dimensionName,
        out ResolvedDimensionReference reference)
    {
        reference = null!;

        if (TryGetDimensionByDirectLookup(model, dimensionName, out var directDimension))
        {
            reference = BuildResolvedDimensionReference(directDimension, fullDimensionNameOverride: dimensionName, resolutionMode: "Exact");
            return true;
        }

        var shortDimensionName = ExtractDimensionLookupKey(dimensionName);
        if (!string.IsNullOrWhiteSpace(shortDimensionName) &&
            !string.Equals(shortDimensionName, dimensionName, StringComparison.OrdinalIgnoreCase) &&
            TryGetDimensionByDirectLookup(model, shortDimensionName, out var shortNameDimension))
        {
            reference = BuildResolvedDimensionReference(shortNameDimension, resolutionMode: "ShortName");
            return true;
        }

        var retitledDimensionName = BuildRetitledDimensionName(model, shortDimensionName, dimensionName);
        if (!string.IsNullOrWhiteSpace(retitledDimensionName) &&
            TryGetDimensionByDirectLookup(model, retitledDimensionName, out var retitledDimension))
        {
            reference = BuildResolvedDimensionReference(retitledDimension, fullDimensionNameOverride: retitledDimensionName, resolutionMode: "Retitle");
            return true;
        }

        var requestedLookupKey = ExtractDimensionLookupKey(dimensionName);
        var fallbackMatches = new List<ResolvedDimensionReference>();

        try
        {
            var feature = model.FirstFeature();
            while (feature is not null)
            {
                if (TryEnumerateFeatureDimensionReferences(feature, out List<ResolvedDimensionReference> featureReferences))
                {
                    foreach (var candidate in featureReferences)
                    {
                        if (string.Equals(candidate.FullDimensionName, dimensionName, StringComparison.OrdinalIgnoreCase))
                        {
                            reference = candidate with { ResolutionMode = "Exact" };
                            return true;
                        }

                        if (!string.IsNullOrWhiteSpace(retitledDimensionName) &&
                            string.Equals(candidate.FullDimensionName, retitledDimensionName, StringComparison.OrdinalIgnoreCase))
                        {
                            reference = candidate with { ResolutionMode = "Retitle" };
                            return true;
                        }

                        if (!string.IsNullOrWhiteSpace(requestedLookupKey) &&
                            string.Equals(ExtractDimensionLookupKey(candidate.FullDimensionName), requestedLookupKey, StringComparison.OrdinalIgnoreCase))
                        {
                            fallbackMatches.Add(candidate with { ResolutionMode = "Enumerated" });
                        }
                    }
                }

                feature = feature.GetNextFeature();
            }
        }
        catch
        {
        }

        if (fallbackMatches.Count == 1)
        {
            reference = fallbackMatches[0];
            return true;
        }

        return false;
    }

    private static bool TryGetDimensionByDirectLookup(
        IModelDoc2 model,
        string dimensionName,
        out IDimension dimension)
    {
        dimension = null!;

        try
        {
            if (model.IParameter(dimensionName) is IDimension typedDimension)
            {
                dimension = typedDimension;
                return true;
            }
        }
        catch
        {
        }

        try
        {
            if (model.Parameter(dimensionName) is IDimension lateBoundDimension)
            {
                dimension = lateBoundDimension;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryEnumerateFeatureDimensionReferences(
        Feature feature,
        out List<ResolvedDimensionReference> references)
    {
        references = [];

        try
        {
            dynamic featureDynamic = feature;
            dynamic displayDimension = featureDynamic.GetFirstDisplayDimension();
            while (displayDimension is not null)
            {
                try
                {
                    dynamic dimension = displayDimension.GetDimension2(0);
                    if (dimension is IDimension typedDimension)
                    {
                        references.Add(BuildResolvedDimensionReference(typedDimension, feature.Name ?? string.Empty));
                    }
                }
                catch
                {
                }

                displayDimension = featureDynamic.GetNextDisplayDimension(displayDimension);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ResolvedDimensionReference BuildResolvedDimensionReference(
        IDimension dimension,
        string featureName = "",
        string? fullDimensionNameOverride = null,
        string resolutionMode = "Enumerated")
    {
        var fullDimensionName = string.IsNullOrWhiteSpace(fullDimensionNameOverride)
            ? SafeToString(() => ((dynamic)dimension).FullName)
            : fullDimensionNameOverride;
        var resolvedFeatureName = string.IsNullOrWhiteSpace(featureName)
            ? ExtractFeatureName(fullDimensionName)
            : featureName;
        var sketchName = ExtractSketchName(fullDimensionName, resolvedFeatureName);

        decimal? currentValue = null;
        if (TryReadDimensionCurrentValue((dynamic)dimension, out double value))
        {
            currentValue = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        return new ResolvedDimensionReference(
            dimension,
            string.IsNullOrWhiteSpace(fullDimensionName) ? SafeToString(() => ((dynamic)dimension).Name) : fullDimensionName,
            resolvedFeatureName,
            sketchName,
            currentValue,
            resolutionMode);
    }

    private static string BuildRetitledDimensionName(IModelDoc2 model, string shortDimensionName, string originalDimensionName)
    {
        if (string.IsNullOrWhiteSpace(shortDimensionName))
        {
            return string.Empty;
        }

        var modelTitle = model.GetTitle() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(modelTitle))
        {
            return string.Empty;
        }

        var titleWithoutExtension = Path.GetFileNameWithoutExtension(modelTitle);
        if (string.IsNullOrWhiteSpace(titleWithoutExtension))
        {
            return string.Empty;
        }

        var suffix = ".Part";
        if (!string.IsNullOrWhiteSpace(originalDimensionName))
        {
            var lastAtIndex = originalDimensionName.LastIndexOf('@');
            if (lastAtIndex >= 0)
            {
                var trailingSegment = originalDimensionName[(lastAtIndex + 1)..];
                if (!string.IsNullOrWhiteSpace(trailingSegment))
                {
                    suffix = trailingSegment;
                }
            }
        }

        return $"{shortDimensionName}@{titleWithoutExtension}{(suffix.StartsWith(".", StringComparison.OrdinalIgnoreCase) ? suffix : $".{suffix}")}";
    }

    private static string ExtractDimensionLookupKey(string dimensionName)
    {
        if (string.IsNullOrWhiteSpace(dimensionName))
        {
            return string.Empty;
        }

        var lastAtIndex = dimensionName.LastIndexOf('@');
        return lastAtIndex < 0 ? dimensionName.Trim() : dimensionName[..lastAtIndex].Trim();
    }

    private static string ExtractFeatureName(string fullDimensionName)
    {
        if (string.IsNullOrWhiteSpace(fullDimensionName))
        {
            return string.Empty;
        }

        var parts = fullDimensionName.Split('@');
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private void ScanDocumentAndChildren(
        string componentName,
        string modelPath,
        IModelDoc2 model,
        SolidWorksDimensionScanResult result,
        Action<string>? logWriter,
        ISet<string> temporaryOpenedDocumentPaths)
    {
        ScanModelDocument(componentName, modelPath, model, result, logWriter);

        if (model is IAssemblyDoc assembly)
        {
            ScanAssemblyComponents(assembly, result, logWriter, temporaryOpenedDocumentPaths);
        }
    }

    private void ScanAssemblyComponents(
        IAssemblyDoc assembly,
        SolidWorksDimensionScanResult result,
        Action<string>? logWriter,
        ISet<string> temporaryOpenedDocumentPaths)
    {
        try
        {
            var components = assembly.GetComponents(false) as object[] ?? [];
            foreach (var componentObject in components)
            {
                if (componentObject is not IComponent2 component)
                {
                    continue;
                }

                ScanComponent(component, result, logWriter, temporaryOpenedDocumentPaths);
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"遍历装配体组件失败：{ex.Message}");
            WriteException(logWriter, "遍历装配体组件失败。", ex);
        }
    }

    private void ScanComponent(
        IComponent2 component,
        SolidWorksDimensionScanResult result,
        Action<string>? logWriter,
        ISet<string> temporaryOpenedDocumentPaths)
    {
        try
        {
            var componentName = component.Name2 ?? string.Empty;
            var modelPath = component.GetPathName() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                result.Errors.Add($"组件缺少文件路径：{componentName}");
                return;
            }

            var model = component.GetModelDoc2() as IModelDoc2;
            if (model is null)
            {
                model = TryOpenComponentModel(modelPath, logWriter, temporaryOpenedDocumentPaths);
            }

            if (model is null)
            {
                result.Errors.Add($"无法读取组件文档：{componentName} ({modelPath})");
                return;
            }

            ScanModelDocument(componentName, modelPath, model, result, logWriter);
        }
        catch (Exception ex)
        {
            var componentName = component.Name2 ?? "未知组件";
            result.Errors.Add($"扫描组件失败：{componentName}，{ex.Message}");
            WriteException(logWriter, $"扫描组件失败：{componentName}。", ex);
        }
    }

    private IModelDoc2? TryOpenComponentModel(
        string modelPath,
        Action<string>? logWriter,
        ISet<string> temporaryOpenedDocumentPaths)
    {
        var extension = Path.GetExtension(modelPath).ToUpperInvariant();
        var documentType = extension switch
        {
            ".SLDPRT" => (int)swDocumentTypes_e.swDocPART,
            ".SLDASM" => (int)swDocumentTypes_e.swDocASSEMBLY,
            _ => 0
        };

        if (documentType == 0)
        {
            WriteLog(logWriter, $"无法识别组件文档类型：{modelPath}");
            return null;
        }

        if (!TryOpenDocumentByCom(
                modelPath,
                documentType,
                logWriter,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly))
        {
            return null;
        }

        temporaryOpenedDocumentPaths.Add(NormalizeFilePath(modelPath));
        return GetOpenedDocumentByPath(modelPath);
    }

    private void ScanModelDocument(
        string componentName,
        string modelPath,
        IModelDoc2 model,
        SolidWorksDimensionScanResult result,
        Action<string>? logWriter)
    {
        try
        {
            var feature = model.FirstFeature();
            while (feature is not null)
            {
                ScanFeatureDimensions(componentName, modelPath, feature, result, logWriter);
                feature = feature.GetNextFeature();
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"扫描文档尺寸失败：{componentName} ({modelPath})，{ex.Message}");
            WriteException(logWriter, $"扫描文档尺寸失败：{componentName}。", ex);
        }
    }

    private void ScanFeatureDimensions(
        string componentName,
        string modelPath,
        Feature feature,
        SolidWorksDimensionScanResult result,
        Action<string>? logWriter)
    {
        try
        {
            dynamic featureDynamic = feature;
            dynamic displayDimension = featureDynamic.GetFirstDisplayDimension();
            while (displayDimension is not null)
            {
                try
                {
                    dynamic dimension = displayDimension.GetDimension2(0);
                    if (dimension is not null)
                    {
                        var fullName = SafeToString(() => dimension.FullName);
                        var dimensionName = ExtractDimensionName(fullName, dimension);
                        var sketchName = ExtractSketchName(fullName, feature.Name ?? string.Empty);
                        var currentValue = TryReadDimensionCurrentValue(dimension, out double value)
                            ? value.ToString("0.###", CultureInfo.InvariantCulture)
                            : string.Empty;

                        result.Items.Add(new SolidWorksDimensionScanItem
                        {
                            ComponentName = componentName,
                            PartFilePath = modelPath,
                            FeatureName = feature.Name ?? string.Empty,
                            SketchName = sketchName,
                            DimensionName = dimensionName,
                            FullDimensionName = string.IsNullOrWhiteSpace(fullName) ? dimensionName : fullName,
                            CurrentValue = currentValue,
                            Unit = "mm"
                        });
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"读取尺寸失败：{componentName} / {feature.Name}，{ex.Message}");
                    WriteException(logWriter, $"读取尺寸失败：{componentName} / {feature.Name}。", ex);
                }

                displayDimension = featureDynamic.GetNextDisplayDimension(displayDimension);
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"遍历特征尺寸失败：{componentName} / {feature.Name}，{ex.Message}");
            WriteException(logWriter, $"遍历特征尺寸失败：{componentName} / {feature.Name}。", ex);
        }
    }

    private static bool TryReadDimensionCurrentValue(dynamic dimension, out double valueInMillimeters)
    {
        valueInMillimeters = 0;

        try
        {
            var raw = dimension.SystemValue;
            if (TryConvertToDouble(raw, out double meters))
            {
                valueInMillimeters = meters * 1000d;
                return true;
            }
        }
        catch
        {
        }

        try
        {
            var raw = dimension.Value;
            if (TryConvertToDouble(raw, out double meters))
            {
                valueInMillimeters = meters * 1000d;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void WriteDimensionEnumerationDiagnostics(string partFilePath, Action<string>? logWriter)
    {
        try
        {
            var items = ListPartDimensionsAsync(partFilePath, logWriter).GetAwaiter().GetResult();
            if (items.Count == 0)
            {
                WriteLog(logWriter, $"未枚举到任何可用尺寸：{partFilePath}");
                return;
            }

            foreach (var item in items)
            {
                WriteLog(
                    logWriter,
                    $"DimensionCandidate: Feature={item.FeatureName}, Sketch={item.SketchName}, FullName={item.FullDimensionName}, CurrentValue={item.CurrentValue} {item.Unit}");
            }
        }
        catch (Exception ex)
        {
            WriteException(logWriter, $"枚举零件可用尺寸失败：{partFilePath}。", ex);
        }
    }

    private IModelDoc2? OpenDocumentReadOnlyForScan(
        string assemblyPath,
        Action<string>? logWriter,
        ISet<string> temporaryOpenedDocumentPaths)
    {
        if (!ValidateExistingFile(assemblyPath, logWriter, "装配体"))
        {
            return null;
        }

        WriteLog(logWriter, $"准备以只读方式打开扫描目标：{assemblyPath}");
        var alreadyOpened = GetOpenedDocumentByPath(assemblyPath);
        if (alreadyOpened is not null)
        {
            ActivateDocument(alreadyOpened, logWriter);
            return alreadyOpened;
        }

        if (!TryOpenDocumentByCom(
                assemblyPath,
                (int)swDocumentTypes_e.swDocASSEMBLY,
                logWriter,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly))
        {
            return null;
        }

        temporaryOpenedDocumentPaths.Add(NormalizeFilePath(assemblyPath));
        return GetOpenedDocumentByPath(assemblyPath);
    }

    private IModelDoc2? GetActiveDocument()
    {
        lock (_syncRoot)
        {
            return _swApp?.ActiveDoc as IModelDoc2;
        }
    }

    private void CloseTemporaryDocuments(ISet<string> documentPaths, Action<string>? logWriter)
    {
        if (documentPaths.Count == 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_swApp is null)
            {
                return;
            }

            foreach (var path in documentPaths)
            {
                try
                {
                    var document = GetOpenedDocumentByPath(path);
                    if (document is null)
                    {
                        continue;
                    }

                    _swApp.CloseDoc(document.GetTitle());
                    WriteLog(logWriter, $"已关闭扫描过程中临时打开的文档：{path}");
                }
                catch (Exception ex)
                {
                    WriteException(logWriter, $"关闭扫描临时文档失败：{path}", ex);
                }
            }
        }
    }

    private static bool TryConvertToDouble(object? value, out double result)
    {
        result = 0;
        if (value is null)
        {
            return false;
        }

        return value switch
        {
            double doubleValue => (result = doubleValue) == doubleValue,
            float floatValue => (result = floatValue) == floatValue,
            decimal decimalValue => (result = (double)decimalValue) == (double)decimalValue,
            int intValue => (result = intValue) == intValue,
            long longValue => (result = longValue) == longValue,
            _ => double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
        };
    }

    private void WriteScanOutputs(SolidWorksDimensionScanResult result)
    {
        File.WriteAllText(result.JsonOutputPath, JsonSerializer.Serialize(result, ScanJsonOptions), Encoding.UTF8);

        var builder = new StringBuilder();
        builder.AppendLine("ComponentName,PartFilePath,FeatureName,SketchName,DimensionName,FullDimensionName,CurrentValue,Unit");
        foreach (var item in result.Items)
        {
            builder.AppendLine(string.Join(",",
                EscapeCsv(item.ComponentName),
                EscapeCsv(item.PartFilePath),
                EscapeCsv(item.FeatureName),
                EscapeCsv(item.SketchName),
                EscapeCsv(item.DimensionName),
                EscapeCsv(item.FullDimensionName),
                EscapeCsv(item.CurrentValue),
                EscapeCsv(item.Unit)));
        }

        File.WriteAllText(result.CsvOutputPath, builder.ToString(), Encoding.UTF8);
    }

    private static string EscapeCsv(string value)
    {
        var safe = value ?? string.Empty;
        if (!safe.Contains(',') && !safe.Contains('"') && !safe.Contains('\n') && !safe.Contains('\r'))
        {
            return safe;
        }

        return $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string SafeToString(Func<object?> getter)
    {
        try
        {
            return getter()?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractDimensionName(string fullDimensionName, dynamic dimension)
    {
        if (!string.IsNullOrWhiteSpace(fullDimensionName))
        {
            var parts = fullDimensionName.Split('@');
            if (parts.Length > 0)
            {
                return parts[0];
            }
        }

        return SafeToString(() => dimension.Name);
    }

    private static string ExtractSketchName(string fullDimensionName, string featureName)
    {
        if (!string.IsNullOrWhiteSpace(fullDimensionName))
        {
            var parts = fullDimensionName.Split('@');
            if (parts.Length >= 2)
            {
                return parts[1];
            }
        }

        return featureName ?? string.Empty;
    }

    private bool TryGetOrCreateApplication(bool allowCreate, Action<string>? logWriter, out SldWorks? application)
    {
        lock (_syncRoot)
        {
            if (_swApp is not null && TryConfigureApplication(_swApp, logWriter))
            {
                application = _swApp;
                WriteLog(logWriter, "已连接到当前会话中的 SolidWorks 实例。");
                return true;
            }

            _swApp = null;

            if (TryGetActiveInstance(logWriter, out var activeInstance) && activeInstance is not null)
            {
                _swApp = activeInstance;
                if (TryConfigureApplication(activeInstance, logWriter))
                {
                    application = activeInstance;
                    WriteLog(logWriter, "已连接到现有 SolidWorks 实例。");
                    return true;
                }

                _swApp = null;
            }

            if (!allowCreate)
            {
                application = null;
                return false;
            }

            var createdInstance = CreateNewInstance(logWriter);
            if (createdInstance is null)
            {
                application = null;
                return false;
            }

            _swApp = createdInstance;
            if (TryConfigureApplication(createdInstance, logWriter))
            {
                application = createdInstance;
                WriteLog(logWriter, "已创建新的 SolidWorks 实例。");
                return true;
            }

            _swApp = null;
            application = null;
            return false;
        }
    }

    private bool TryActivateOpenedDocument(string documentPath, Action<string>? logWriter)
    {
        try
        {
            lock (_syncRoot)
            {
                if (_swApp is null)
                {
                    return false;
                }

                var openedDocument = GetOpenedDocumentByPath(documentPath);
                if (openedDocument is null)
                {
                    return false;
                }

                ActivateDocument(openedDocument, logWriter);
                WriteLog(logWriter, $"检测到文档已打开，已激活：{openedDocument.GetTitle()}");
                return true;
            }
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "激活已打开的 SolidWorks 文档失败。", ex);
            return false;
        }
    }

    private bool TryOpenDocumentByCom(
        string documentPath,
        int documentType,
        Action<string>? logWriter,
        int openOptions = (int)swOpenDocOptions_e.swOpenDocOptions_Silent)
    {
        try
        {
            lock (_syncRoot)
            {
                if (_swApp is null)
                {
                    return false;
                }

                var errors = 0;
                var warnings = 0;
                var model = _swApp.OpenDoc6(
                    documentPath,
                    documentType,
                    openOptions,
                    string.Empty,
                    ref errors,
                    ref warnings);

                if (model is null)
                {
                    WriteLog(logWriter, $"COM 打开文档失败：Path={documentPath}，Errors={errors}，Warnings={warnings}");
                    return false;
                }

                WriteLog(logWriter, $"COM 打开文档成功：Path={documentPath}，Errors={errors}，Warnings={warnings}");
                return true;
            }
        }
        catch (Exception ex)
        {
            WriteException(logWriter, $"通过 COM 打开文档失败：{documentPath}", ex);
            return false;
        }
    }

    private async Task<bool> TryOpenDocumentByUiAutomationAsync(
        string documentPath,
        Action<string>? logWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            var solidWorksProcess = GetLatestSolidWorksProcess();
            if (solidWorksProcess is null)
            {
                WriteLog(logWriter, "未检测到 SolidWorks 进程，无法执行界面自动化打开。");
                return false;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true);
            var shell = Activator.CreateInstance(shellType!);
            if (shell is null)
            {
                WriteLog(logWriter, "无法创建 WScript.Shell 对象。");
                return false;
            }

            WriteLog(logWriter, $"开始模拟人工打开流程，SolidWorks PID={solidWorksProcess.Id}");

            if (!TrySetClipboardText(documentPath, logWriter))
            {
                return false;
            }

            var activated = (bool)(shell.GetType().InvokeMember(
                "AppActivate",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { solidWorksProcess.Id }) ?? false);

            if (!activated)
            {
                WriteLog(logWriter, "无法激活 SolidWorks 窗口。");
                return false;
            }

            await Task.Delay(1200, cancellationToken);
            SendShellKeys(shell, "^o");
            await Task.Delay(2500, cancellationToken);
            SendShellKeys(shell, "^v");
            await Task.Delay(1000, cancellationToken);
            SendShellKeys(shell, "{ENTER}");

            WriteLog(logWriter, $"已通过界面自动化提交路径：{documentPath}");
            await Task.Delay(8000, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "通过界面自动化打开 SolidWorks 文档失败。", ex);
            return false;
        }
    }

    private IModelDoc2? GetOpenedDocumentByPath(string documentPath)
    {
        lock (_syncRoot)
        {
            if (_swApp is null)
            {
                return null;
            }

            var normalizedPath = NormalizeFilePath(documentPath);
            var byFullPath = _swApp.GetOpenDocument(normalizedPath);
            if (byFullPath is IModelDoc2 fullPathDocument)
            {
                return fullPathDocument;
            }

            return GetOpenDocuments(_swApp)
                .FirstOrDefault(document =>
                    string.Equals(
                        NormalizeFilePath(document.GetPathName()),
                        normalizedPath,
                        StringComparison.OrdinalIgnoreCase));
        }
    }

    private static List<IModelDoc2> GetOpenDocuments(SldWorks application)
    {
        var documents = new List<IModelDoc2>();
        var current = application.GetFirstDocument() as IModelDoc2
            ?? application.IGetFirstDocument2() as IModelDoc2;

        while (current is not null)
        {
            documents.Add(current);
            current = current.GetNext() as IModelDoc2;
        }

        return documents;
    }

    private static int GetDocumentClosePriority(IModelDoc2 document)
    {
        return document.GetType() switch
        {
            (int)swDocumentTypes_e.swDocASSEMBLY => 2,
            (int)swDocumentTypes_e.swDocPART => 1,
            _ => 0
        };
    }

    private static bool IsAssemblyReferencingFolder(IModelDoc2 document, string normalizedFolder)
    {
        if (document is not IAssemblyDoc assemblyDocument)
        {
            return false;
        }

        try
        {
            var components = assemblyDocument.GetComponents(false) as object[] ?? [];
            foreach (var componentObject in components)
            {
                if (componentObject is not IComponent2 component)
                {
                    continue;
                }

                if (IsPathUnderFolder(component.GetPathName(), normalizedFolder))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private bool ValidateActiveDocumentPath(string expectedAssemblyPath, Action<string>? logWriter)
    {
        var actualPath = GetActiveDocumentPath();
        WriteLog(logWriter, $"实际打开路径：{actualPath}");

        if (!string.Equals(
                NormalizeFilePath(expectedAssemblyPath),
                NormalizeFilePath(actualPath),
                StringComparison.OrdinalIgnoreCase))
        {
            WriteLog(logWriter, $"打开路径校验失败：Expected={NormalizeFilePath(expectedAssemblyPath)}, Actual={NormalizeFilePath(actualPath)}");
            return false;
        }

        return true;
    }

    private void ActivateDocument(IModelDoc2 document, Action<string>? logWriter)
    {
        try
        {
            if (document is null)
            {
                return;
            }

            if (_swApp is null)
            {
                return;
            }

            var errors = 0;
            _swApp.ActivateDoc3(
                document.GetTitle(),
                true,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                ref errors);

            WriteLog(logWriter, $"已激活文档：{document.GetTitle()}，ActivateErrors={errors}");
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "激活 SolidWorks 文档失败。", ex);
        }
    }

    private bool SaveDocument(IModelDoc2 document, Action<string>? logWriter)
    {
        try
        {
            if (!ValidateSavePermission(document, logWriter, out var denialMessage))
            {
                WriteLog(logWriter, denialMessage);
                return false;
            }

            var errors = 0;
            var warnings = 0;
            var saveSucceeded = document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
            WriteLog(logWriter, $"保存文档结果：Path={document.GetPathName()}，Save={saveSucceeded}，Errors={errors}，Warnings={warnings}");
            return saveSucceeded;
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "保存 SolidWorks 文档失败。", ex);
            return false;
        }
    }

    private bool ValidateSavePermission(IModelDoc2 document, Action<string>? logWriter, out string denialMessage)
    {
        denialMessage = string.Empty;

        var rawDocumentPath = document.GetPathName() ?? string.Empty;
        var currentDocumentPath = string.IsNullOrWhiteSpace(rawDocumentPath)
            ? string.Empty
            : NormalizeFilePath(rawDocumentPath);

        logWriter?.Invoke($"[Safety] 当前文档路径 = {currentDocumentPath}");
        logWriter?.Invoke($"[Safety] 初始模型目录 = {_initialModelFolder}");
        logWriter?.Invoke($"[Safety] 新模型目录 = {_workingModelFolder}");

        if (string.IsNullOrWhiteSpace(currentDocumentPath))
        {
            logWriter?.Invoke("[Safety] 保存权限校验失败");
            denialMessage = "当前模型不在受控工作区内，禁止自动修改。";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_initialModelFolder) &&
            IsPathUnderFolder(currentDocumentPath, _initialModelFolder))
        {
            logWriter?.Invoke("[Safety] 保存权限校验失败");
            denialMessage = "安全保护：禁止修改初始模型，请先打开即将修改的模型。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_workingModelFolder) ||
            !IsPathUnderFolder(currentDocumentPath, _workingModelFolder))
        {
            logWriter?.Invoke("[Safety] 保存权限校验失败");
            denialMessage = "当前模型不在受控工作区内，禁止自动修改。";
            return false;
        }

        logWriter?.Invoke("[Safety] 保存权限校验通过");
        return true;
    }

    private static async Task WaitForSolidWorksStartupAsync(Action<string>? logWriter, CancellationToken cancellationToken)
    {
        try
        {
            var process = Process.GetProcessesByName("SLDWORKS")
                .OrderByDescending(GetProcessStartTimeSafe)
                .FirstOrDefault();

            if (process is not null)
            {
                try
                {
                    process.WaitForInputIdle((int)StartupWait.TotalMilliseconds);
                    WriteLog(logWriter, "SolidWorks 主界面已进入可交互状态。");
                    return;
                }
                catch
                {
                }
            }

            await Task.Delay(StartupWait, cancellationToken);
            WriteLog(logWriter, "已完成 SolidWorks 启动等待。");
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "等待 SolidWorks 初始化时发生异常。", ex);
            await Task.Delay(StartupWait, cancellationToken);
        }
    }

    private static bool TryGetActiveInstance(Action<string>? logWriter, out SldWorks? activeInstance)
    {
        activeInstance = null;

        try
        {
            if (!TryGetProgIdClsid(out var clsid))
            {
                WriteLog(logWriter, $"未找到 ProgID：{ProgId}");
                return false;
            }

            var result = GetActiveObject(ref clsid, IntPtr.Zero, out var activeObject);
            if (result == 0 && activeObject is SldWorks swApp)
            {
                activeInstance = swApp;
                return true;
            }

            WriteLog(logWriter, $"未连接到现有 SolidWorks 实例，HRESULT=0x{result:X8}");
            return false;
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "连接现有 SolidWorks 实例失败。", ex);
            activeInstance = null;
            return false;
        }
    }

    private bool TryGetRunningApplication(out SldWorks? application)
    {
        lock (_syncRoot)
        {
            if (_swApp is not null)
            {
                try
                {
                    _ = _swApp.ActiveDoc;
                    application = _swApp;
                    return true;
                }
                catch
                {
                    _swApp = null;
                }
            }
        }

        if (TryGetActiveInstance(logWriter: null, out var activeInstance) && activeInstance is not null)
        {
            lock (_syncRoot)
            {
                _swApp = activeInstance;
            }

            application = activeInstance;
            return true;
        }

        application = null;
        return false;
    }

    private static SldWorks? CreateNewInstance(Action<string>? logWriter)
    {
        try
        {
            WriteLog(logWriter, $"正在通过 CreateObject 创建 SolidWorks 实例：{ProgId}");
            return (SldWorks)Interaction.CreateObject(ProgId);
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "创建新的 SolidWorks 实例失败。", ex);
            return null;
        }
    }

    private static bool TryConfigureApplication(SldWorks application, Action<string>? logWriter)
    {
        try
        {
            application.Visible = true;
            application.UserControl = true;
            WriteLog(logWriter, "已设置 SolidWorks 为可见并交由用户控制。");
            return true;
        }
        catch (Exception ex)
        {
            WriteException(logWriter, "配置 SolidWorks 可见性失败。", ex);
            return false;
        }
    }

    private static string? ResolveInitialInspectionCarAssemblyPath(Action<string>? logWriter)
    {
        var directoryPath = Path.GetDirectoryName(InitialInspectionCarAssemblyPath) ?? string.Empty;
        var directoryExists = Directory.Exists(directoryPath);
        var fileExists = File.Exists(InitialInspectionCarAssemblyPath);

        WriteLog(
            logWriter,
            $"路径诊断：DirectoryExists={directoryExists}，FileExists={fileExists}，Path={InitialInspectionCarAssemblyPath}");

        return fileExists ? InitialInspectionCarAssemblyPath : null;
    }

    private static bool TrySetClipboardText(string text, Action<string>? logWriter)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                WriteClipboardTextOnStaThread(text);
                WriteLog(logWriter, $"已将模型路径写入剪贴板，第 {attempt} 次成功。");
                return true;
            }
            catch (Exception ex) when (IsClipboardBusy(ex))
            {
                lastException = ex;
                WriteLog(logWriter, $"剪贴板被占用，第 {attempt} 次重试。");
                Thread.Sleep(800);
            }
            catch (Exception ex)
            {
                lastException = ex;
                WriteException(logWriter, "写入剪贴板失败。", ex);
                break;
            }
        }

        WriteLog(logWriter, "多次重试后仍无法写入剪贴板。");
        if (lastException is not null)
        {
            WriteException(logWriter, "最终仍无法写入剪贴板。", lastException);
        }

        return false;
    }

    private static void WriteClipboardTextOnStaThread(string text)
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                Forms.Clipboard.SetDataObject(text, true, 20, 200);
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadException is not null)
        {
            throw threadException;
        }
    }

    private static bool IsClipboardBusy(Exception exception)
    {
        return exception is COMException comException && (uint)comException.HResult == 0x800401D0;
    }

    private static bool ValidateExistingFile(string path, Action<string>? logWriter, string fileType)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            WriteLog(logWriter, $"{fileType}路径为空。");
            return false;
        }

        var fullPath = NormalizeFilePath(path);
        var exists = File.Exists(fullPath);
        WriteLog(logWriter, $"{fileType}路径检查：Exists={exists}，Path={fullPath}");
        return exists;
    }

    private static bool TryParseNumericValue(string value, out double numericValue)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out numericValue)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out numericValue);
    }

    private static double ConvertToSystemValue(double numericValue, string unit)
    {
        return NormalizeUnit(unit).ToLowerInvariant() switch
        {
            "mm" => numericValue / 1000d,
            "cm" => numericValue / 100d,
            "m" => numericValue,
            _ => numericValue
        };
    }

    private static string NormalizeUnit(string unit)
    {
        return string.IsNullOrWhiteSpace(unit) ? "mm" : unit.Trim();
    }

    private static bool IsPathUnderFolder(string? filePath, string normalizedFolder)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var normalizedFilePath = NormalizeFilePath(filePath);
        return normalizedFilePath.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFilePath(string path)
    {
        return Path.GetFullPath(path.Trim());
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var normalizedPath = NormalizeFilePath(path);
        return normalizedPath.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedPath
            : normalizedPath + Path.DirectorySeparatorChar;
    }

    private static void SendShellKeys(object shell, string keys)
    {
        shell.GetType().InvokeMember(
            "SendKeys",
            System.Reflection.BindingFlags.InvokeMethod,
            null,
            shell,
            new object[] { keys });
    }

    private static Process? GetLatestSolidWorksProcess()
    {
        return Process.GetProcessesByName("SLDWORKS")
            .OrderByDescending(GetProcessStartTimeSafe)
            .FirstOrDefault();
    }

    private static DateTime GetProcessStartTimeSafe(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static bool TryGetProgIdClsid(out Guid clsid)
    {
        var result = CLSIDFromProgIDEx(ProgId, out clsid);
        if (result == 0)
        {
            return true;
        }

        result = CLSIDFromProgID(ProgId, out clsid);
        return result == 0;
    }

    private static void WriteLog(Action<string>? logWriter, string message)
    {
        logWriter?.Invoke($"[SolidWorks] {message}");
    }

    private static void WriteException(Action<string>? logWriter, string title, Exception exception)
    {
        WriteLog(logWriter, $"{title}{exception.Message} (0x{exception.HResult:X8})");
        App.WriteStartupLog(title, exception);
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgIDEx(string progId, out Guid clsid);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);
}
