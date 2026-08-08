using System.Globalization;
using System.IO;
using System.Text;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class CommandDispatcher
{
    private const string QueryEditableParametersAction = "query_editable_parameters";
    private const string ResetModelWorkspaceAction = "reset_model_workspace";
    private const string ScanSolidWorksDimensionsAction = "scan_solidworks_dimensions";
    private const string RecommendTrussParameterMappingAction = "recommend_truss_parameter_mapping";
    private const string ConfirmTrussParameterMappingAction = "confirm_truss_parameter_mapping";
    private const string OpenWorkingModelAction = "open_working_model";
    private const string UpdateSolidWorksDimensionsAction = "update_solidworks_dimensions";
    private const string ValueModeOuterMinusTwoThickness = "OuterMinusTwoThickness";
    private const string LowerTrussMemberId = EditableTrussMemberCatalogService.LowerTrussMemberId;
    private const string LockedLowerTrussModelPath = @"E:\反力架\反力架\三六重工v2\不伸缩单层检查车\Solidworks模型\初始模型\桁架2\1-7方管70×70×5-1159.SLDPRT";
    private const string LinkedLowerTrussModelPath = @"E:\反力架\反力架\三六重工v2\不伸缩单层检查车\Solidworks模型\新模型\桁架2\1-方管70×70×5-1159.SLDPRT";
    private const string LinkedLowerTrussSectionModelPath = @"E:\反力架\反力架\三六重工v2\不伸缩单层检查车\Solidworks模型\新模型\桁架1\1-方管70×70×5-1530.SLDPRT";
    private const string LinkedLowerTrussSectionPartName = "1-方管70×70×5-1530.Part";
    private const string LinkedLowerTrussSectionWidthDimension = "D1@草图1@1-方管70×70×5-1530.Part";
    private const string LinkedLowerTrussSectionHeightDimension = "D2@草图1@1-方管70×70×5-1530.Part";
    private const string LinkedLowerTrussSectionThicknessDimension = "D5@拉伸-薄壁1@1-方管70×70×5-1530.Part";
    private const string LinkedLowerChordPartRelativePath = @"新模型\桁架2\1-方管70×70×5-1159.SLDPRT";
    private const string LinkedLowerChordPartRelativeWithNewModel = @"新模型\桁架2\1-方管70×70×5-1159.SLDPRT";
    private const string LinkedLowerChordPartName = "1-方管70×70×5-1159.Part";
    private const string LinkedLowerChordOuterWidthDimension = "D3@草图2@1-方管70×70×5-1159.Part";
    private const string LinkedLowerChordOuterHeightDimension = "D1@草图2@1-方管70×70×5-1159.Part";
    private const string LinkedLowerChordInnerWidthDimension = "D4@草图2@1-方管70×70×5-1159.Part";
    private const string LinkedLowerChordInnerHeightDimension = "D2@草图2@1-方管70×70×5-1159.Part";
    private const string LowerSectionLinkedCompensationPartName = "20-下-方管70×70×5-1280.Part";
    private const string LowerSectionLinkedCompensationWidthDimension = "D1@草图1@20-下-方管70×70×5-1280.Part";
    private const string LowerSectionLinkedCompensationHeightDimension = "D2@草图1@20-下-方管70×70×5-1280.Part";
    private readonly EditableTrussMemberCatalogService _editableTrussMemberCatalogService;
    private readonly ISolidWorksService _solidWorksService;
    private readonly TrussMemberCommandParser _trussMemberCommandParser;
    private readonly ModelWorkspaceManager _workspaceManager;
    private readonly DimensionScanCatalogService _dimensionScanCatalogService;
    private readonly TrussParameterMappingRecommendationService _trussParameterMappingRecommendationService;
    private readonly TrussParameterSetupStateService _trussParameterSetupStateService;

    public CommandDispatcher(
        ISolidWorksService solidWorksService,
        ModelWorkspaceManager workspaceManager,
        EditableTrussMemberCatalogService editableTrussMemberCatalogService,
        TrussMemberCommandParser trussMemberCommandParser,
        DimensionScanCatalogService dimensionScanCatalogService,
        TrussParameterMappingRecommendationService trussParameterMappingRecommendationService,
        TrussParameterSetupStateService trussParameterSetupStateService)
    {
        _solidWorksService = solidWorksService;
        _workspaceManager = workspaceManager;
        _editableTrussMemberCatalogService = editableTrussMemberCatalogService;
        _trussMemberCommandParser = trussMemberCommandParser;
        _dimensionScanCatalogService = dimensionScanCatalogService;
        _trussParameterMappingRecommendationService = trussParameterMappingRecommendationService;
        _trussParameterSetupStateService = trussParameterSetupStateService;
    }

    public async Task<CommandDispatchResult> ExecuteAsync(
        LlmParseResult? parseResult,
        IReadOnlyList<ParameterItem> parameters,
        string rawUserInput,
        Action<string> logWriter,
        CancellationToken cancellationToken = default)
    {
        if (parseResult is null || parseResult.Actions.Count == 0)
        {
            return CommandDispatchResult.NotHandled;
        }

        var orderedActions = GetOrderedActions(parseResult.Actions);
        if (orderedActions.Count == 0)
        {
            return CommandDispatchResult.NotHandled;
        }

        var assistantMessages = new List<string>();
        var handled = false;
        var requiresFollowUp = false;
        var anyHandledAction = false;
        var succeeded = true;
        SolidWorksDimensionScanResult? latestScanResult = null;

        foreach (var action in orderedActions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (action)
            {
                case QueryEditableParametersAction:
                    handled = true;
                    anyHandledAction = true;
                    HandleQueryEditableParameters(assistantMessages, logWriter, rawUserInput);
                    break;

                case ResetModelWorkspaceAction:
                    handled = true;
                    anyHandledAction = true;
                    await HandleResetWorkspaceAsync(assistantMessages, logWriter, cancellationToken);
                    break;

                case ScanSolidWorksDimensionsAction:
                    handled = true;
                    anyHandledAction = true;
                    latestScanResult = await HandleScanSolidWorksDimensionsAsync(assistantMessages, logWriter, cancellationToken);
                    break;

                case RecommendTrussParameterMappingAction:
                    handled = true;
                    anyHandledAction = true;
                    HandleRecommendTrussParameterMapping(assistantMessages, logWriter, rawUserInput);
                    break;

                case ConfirmTrussParameterMappingAction:
                    handled = true;
                    anyHandledAction = true;
                    HandleConfirmTrussParameterMapping(assistantMessages, logWriter);
                    break;

                case OpenWorkingModelAction:
                    handled = true;
                    anyHandledAction = true;
                    var openSucceeded = await HandleOpenWorkingModelAsync(rawUserInput, assistantMessages, logWriter, cancellationToken);
                    succeeded &= openSucceeded;
                    break;

                case UpdateSolidWorksDimensionsAction:
                    var updateResult = await HandleUpdateDimensionsAsync(
                        parseResult,
                        parameters,
                        rawUserInput,
                        assistantMessages,
                        logWriter,
                        cancellationToken);
                    handled |= updateResult.Handled;
                    requiresFollowUp |= updateResult.RequiresFollowUp;
                    if (updateResult.Handled)
                    {
                        anyHandledAction = true;
                        succeeded &= updateResult.Succeeded;
                    }
                    break;
            }
        }

        return new CommandDispatchResult(
            handled,
            handled && !requiresFollowUp,
            anyHandledAction && succeeded,
            assistantMessages,
            latestScanResult);
    }

    private void HandleRecommendTrussParameterMapping(
        ICollection<string> assistantMessages,
        Action<string> logWriter,
        string rawUserInput)
    {
        logWriter("开始执行动作：recommend_truss_parameter_mapping");
        assistantMessages.Add(_editableTrussMemberCatalogService.BuildQueryableEditableParametersReply(rawUserInput));
    }

    private void HandleConfirmTrussParameterMapping(
        ICollection<string> assistantMessages,
        Action<string> logWriter)
    {
        logWriter("开始执行动作：confirm_truss_parameter_mapping");

        if (!_trussParameterMappingRecommendationService.TryBuildConfirmedMappings(out var mappings, out var failureMessage))
        {
            assistantMessages.Add(failureMessage);
            logWriter($"确认推荐映射失败：{failureMessage}");
            return;
        }

        assistantMessages.Add(_trussParameterMappingRecommendationService.BuildConfirmPreviewReply(mappings));

        try
        {
            var configPath = _editableTrussMemberCatalogService.GetConfigPath();
            BackupEditableTrussMemberConfig(configPath, logWriter);

            var updatedMembers = _editableTrussMemberCatalogService.GetAllMembers()
                .Select(CloneEditableTrussMemberItem)
                .ToList();

            foreach (var mapping in mappings)
            {
                var member = updatedMembers.FirstOrDefault(item =>
                    string.Equals(item.Id, mapping.MemberId, StringComparison.OrdinalIgnoreCase));
                if (member is null)
                {
                        logWriter($"未找到待写入的桁架构件配置：{mapping.MemberId}");
                    continue;
                }

                member.PartFilePath = mapping.PartFilePath;
                member.ComponentName = mapping.ComponentName;
                member.WidthDimensionName = mapping.WidthDimensionName;
                member.HeightDimensionName = mapping.HeightDimensionName;
                member.ThicknessDimensionName = mapping.ThicknessDimensionName;
            }

            _editableTrussMemberCatalogService.SaveMembers(updatedMembers);
            _editableTrussMemberCatalogService.Reload();

            assistantMessages.Add("""
桁架参数映射已保存。

现在你可以尝试：
- 把桁架上弦杆截面改成 80x80x4
- 把桁架下弦杆截面改成 100x100x5
- 把上下弦杆截面都改成 80x80x4
""".Trim());
            logWriter("推荐桁架参数映射已写入 editable-truss-members.json 并重新加载。");
        }
        catch (Exception ex)
        {
            logWriter($"保存推荐桁架参数映射失败：{ex}");
            assistantMessages.Add("保存推荐映射失败，请检查高级调试日志。");
        }
    }

    private void HandleQueryEditableParameters(
        ICollection<string> assistantMessages,
        Action<string> logWriter,
        string rawUserInput)
    {
        logWriter("开始执行动作：query_editable_parameters");
        assistantMessages.Add(_editableTrussMemberCatalogService.BuildQueryableEditableParametersReply(rawUserInput));
    }

    private async Task HandleResetWorkspaceAsync(
        ICollection<string> assistantMessages,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            logWriter("开始执行动作：reset_model_workspace");

            var closeSucceeded = await _solidWorksService.CloseDocumentsUnderFolderAsync(
                _workspaceManager.WorkingModelFolder,
                logWriter,
                cancellationToken);

            if (!closeSucceeded)
            {
                assistantMessages.Add("未能自动关闭工作区相关文件，请先手动关闭后再继续。");
                logWriter("关闭 WorkingModelFolder 下的文档未完全成功，继续尝试重置工作区。");
            }

            await _workspaceManager.ResetWorkspaceAsync(logWriter);
            assistantMessages.Add("模型已重置完成。你现在可以继续输入要修改的尺寸参数，或让我直接打开工作模型。");
            logWriter($"工作区重置完成：{_workspaceManager.WorkingModelFolder}");
        }
        catch (Exception ex)
        {
            logWriter($"执行 reset_model_workspace 失败：{ex.Message}");
            assistantMessages.Add("模型重置失败，请检查高级调试日志，并确认新模型文件未被占用。");
        }
    }

    private async Task<bool> HandleOpenWorkingModelAsync(
        string rawUserInput,
        ICollection<string> assistantMessages,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            logWriter("开始执行动作：open_working_model");

            var requestedModelReset = ShouldForceReinitializeWorkspace(rawUserInput);
            var reinitializeWorkspace = requestedModelReset || !_workspaceManager.IsWorkspaceInitialized;

            logWriter($"[Workspace] RequestedModelReset={requestedModelReset}");
            logWriter($"[Workspace] SourceTemplatePath={_workspaceManager.InitialModelFolder}");
            logWriter($"[Workspace] WorkingModelPath={_workspaceManager.WorkingModelFolder}");
            logWriter($"[Workspace] ReinitializeWorkspace={reinitializeWorkspace}");

            if (requestedModelReset)
            {
                await _workspaceManager.EnsureWorkingModelAvailableForOpenAsync(logWriter);
            }
            else
            {
                await _workspaceManager.EnsureWorkspaceInitializedAsync(logWriter);
            }

            var assemblyPath = _workspaceManager.GetWorkingAssemblyPath();
            logWriter($"[Workspace] FinalAssemblyPath={assemblyPath}");

            var opened = await _solidWorksService.OpenAssemblyAsync(assemblyPath, logWriter, cancellationToken);
            if (opened)
            {
                assistantMessages.Add("已从初始模型重新生成未修改的工作副本，并打开该工作模型。");
                return true;
            }

            assistantMessages.Add("打开工作模型失败，请检查 SolidWorks、工作区模型文件和许可证状态。");
            return false;
        }
        catch (Exception ex)
        {
            logWriter($"执行 open_working_model 失败：{ex}");
            assistantMessages.Add(ex.Message.Contains("占用", StringComparison.OrdinalIgnoreCase) ||
                                  ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                ? "工作区文件被占用，无法重新打开模型。请先关闭 SolidWorks 中已打开的相关模型后重试。"
                : ex.Message);
            return false;
        }
    }

    private static bool ShouldForceReinitializeWorkspace(string rawUserInput)
    {
        if (string.IsNullOrWhiteSpace(rawUserInput))
        {
            return false;
        }

        var normalized = rawUserInput
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized.Contains("重新", StringComparison.Ordinal) ||
               normalized.Contains("最初", StringComparison.Ordinal) ||
               normalized.Contains("未修改", StringComparison.Ordinal) ||
               normalized.Contains("恢复", StringComparison.Ordinal) ||
               normalized.Contains("重置", StringComparison.Ordinal) ||
               normalized.Contains("原始", StringComparison.Ordinal) ||
               normalized.Contains("初始", StringComparison.Ordinal);
    }

    private async Task<SolidWorksDimensionScanResult?> HandleScanSolidWorksDimensionsAsync(
        ICollection<string> assistantMessages,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            logWriter("开始执行动作：scan_solidworks_dimensions");

            var assemblyPath = _workspaceManager.GetWorkingAssemblyPath();
            var scanResult = await _solidWorksService.ScanDimensionsAsync(assemblyPath, logWriter, cancellationToken);
            assistantMessages.Add(_editableTrussMemberCatalogService.BuildQueryableEditableParametersReply());

            return scanResult;
        }
        catch (Exception ex)
        {
            logWriter($"执行 scan_solidworks_dimensions 失败：{ex}");
            assistantMessages.Add("模型尺寸扫描失败，请检查高级调试日志。");
            return null;
        }
    }

    private async Task<DimensionUpdateDispatchResult> HandleUpdateDimensionsAsync(
        LlmParseResult? parseResult,
        IReadOnlyList<ParameterItem> parameters,
        string rawUserInput,
        ICollection<string> assistantMessages,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            logWriter("开始执行动作：update_solidworks_dimensions");

            if (_trussMemberCommandParser.TryParse(rawUserInput, out var trussParseResult))
            {
                if (trussParseResult.Width.HasValue)
                {
                    logWriter($"ParsedSectionWidth={trussParseResult.Width.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                if (trussParseResult.Height.HasValue)
                {
                    logWriter($"ParsedSectionHeight={trussParseResult.Height.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                if (trussParseResult.Thickness.HasValue)
                {
                    logWriter($"ParsedThicknessValue={trussParseResult.Thickness.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                return await HandleTrussMemberUpdateAsync(
                    trussParseResult,
                    assistantMessages,
                    logWriter,
                    cancellationToken);
            }

            var updateRequests = BuildDimensionUpdateRequests(parameters, logWriter);
            await AppendLinkedUpperChordRedTubeCompensationRequestsAsync(
                updateRequests,
                parameters,
                logWriter,
                cancellationToken);
            if (updateRequests.Count == 0)
            {
                assistantMessages.Add("当前没有可执行的 SolidWorks 尺寸修改请求。");
                return DimensionUpdateDispatchResult.FollowUpRequired;
            }

            var success = await _solidWorksService.UpdateDimensionsAsync(updateRequests, logWriter, cancellationToken);
            if (success)
            {
                assistantMessages.Add("SolidWorks 尺寸已更新。");
                return new DimensionUpdateDispatchResult(true, false, true);
            }

            assistantMessages.Add("SolidWorks 尺寸更新失败，请检查高级调试日志。");
            return new DimensionUpdateDispatchResult(true, false, false);
        }
        catch (OperationCanceledException)
        {
            logWriter("Transaction cancelled during validation");
            assistantMessages.Add("SolidWorks 尺寸更新已取消。");
            return new DimensionUpdateDispatchResult(true, false, false);
        }
        catch (Exception ex)
        {
            logWriter($"执行 update_solidworks_dimensions 失败：{ex}");
            if (ex.Message.Contains("已中止：检测到目标文件不在工作区目录", StringComparison.OrdinalIgnoreCase))
            {
                assistantMessages.Add("已中止：检测到目标文件不在工作区目录，未保存，防止误改原始模型。");
                return new DimensionUpdateDispatchResult(true, false, false);
            }

            assistantMessages.Add(IsMissingModelPathException(ex)
                ? "请先在 SolidWorks 中打开要修改的模型。"
                : $"SolidWorks 尺寸更新失败：{ex.Message}");
            return new DimensionUpdateDispatchResult(true, false, false);
        }
    }

    private async Task<DimensionUpdateDispatchResult> HandleTrussMemberUpdateAsync(
        TrussMemberCommandParseResult parseResult,
        ICollection<string> assistantMessages,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        var targetMembers = parseResult.TargetMemberIds
            .Select(_editableTrussMemberCatalogService.FindEnabledMemberById)
            .Where(member => member is not null)
            .Cast<EditableTrussMemberItem>()
            .ToList();

        if (targetMembers.Count == 0)
        {
            assistantMessages.Add(_editableTrussMemberCatalogService.BuildUnknownMemberReply());
            return DimensionUpdateDispatchResult.FollowUpRequired;
        }

        var unmappedMember = targetMembers.FirstOrDefault(member =>
            !_editableTrussMemberCatalogService.HasAnySolidWorksMapping(member));
        if (unmappedMember is not null)
        {
            assistantMessages.Add(_editableTrussMemberCatalogService.BuildMissingMappingHintText(unmappedMember, parseResult));
            return DimensionUpdateDispatchResult.FollowUpRequired;
        }

        var profileDrivenMember = targetMembers.FirstOrDefault(_editableTrussMemberCatalogService.IsProfileDrivenWeldment);
        if (profileDrivenMember is not null)
        {
            assistantMessages.Add(_editableTrussMemberCatalogService.BuildProfileDrivenModelingHintText(profileDrivenMember));
            return DimensionUpdateDispatchResult.FollowUpRequired;
        }

        var lowerTrussReportContext = await TryCreateLowerTrussReportContextAsync(
            targetMembers,
            parseResult,
            logWriter,
            cancellationToken);
        if (targetMembers.Any(member => string.Equals(member.Id, LowerTrussMemberId, StringComparison.OrdinalIgnoreCase)))
        {
            logWriter("[Intent] DeterministicLowerTrussLinkedUpdate=True");
            if (parseResult.Width.HasValue)
            {
                logWriter($"[LowerTruss] Width={parseResult.Width.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            if (parseResult.Height.HasValue)
            {
                logWriter($"[LowerTruss] Height={parseResult.Height.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            if (parseResult.Thickness.HasValue)
            {
                logWriter($"[LowerTruss] Thickness={parseResult.Thickness.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (lowerTrussReportContext is not null)
        {
            var backupResult = TryBackupLowerTrussModel(lowerTrussReportContext.ModelPath, logWriter);
            if (!backupResult.Succeeded)
            {
                assistantMessages.Add(backupResult.Message);
                return new DimensionUpdateDispatchResult(true, false, false);
            }

            lowerTrussReportContext = lowerTrussReportContext with
            {
                BackupPath = backupResult.BackupPath
            };
        }

        var updateRequests = await BuildTrussMemberDimensionUpdateRequestsWithPartMappingsAsync(
            targetMembers,
            parseResult,
            logWriter,
            cancellationToken);
        var lowerChordLinkedPartPlan = await BuildLinkedLowerChordPartPlanAsync(
            targetMembers,
            parseResult,
            logWriter,
            cancellationToken);
        var lowerSectionLinkedCompensationPlan = await BuildLowerSectionLinkedCompensationPlanAsync(
            targetMembers,
            parseResult,
            logWriter,
            cancellationToken);
        var lowerSection1530LinkedPartPlan = await BuildLowerTrussSection1530LinkedPartPlanAsync(
            updateRequests,
            targetMembers,
            parseResult,
            logWriter,
            cancellationToken);

        logWriter($"ThicknessUpdateRequested={parseResult.Thickness.HasValue}");

        await AppendLinkedUpperChordRedTubeCompensationRequestsAsync(
            updateRequests,
            targetMembers,
            parseResult,
            logWriter,
            cancellationToken);

        if (updateRequests.Count == 0)
        {
            assistantMessages.Add("当前没有生成可执行的桁架构件尺寸修改请求。");
            return DimensionUpdateDispatchResult.FollowUpRequired;
        }

        logWriter($"[LowerChord] TotalRequestsBeforeLinkedPart={updateRequests.Count}");
        if (lowerChordLinkedPartPlan.Enabled && lowerChordLinkedPartPlan.PartFound && lowerChordLinkedPartPlan.Requests.Count > 0)
        {
            foreach (var request in lowerChordLinkedPartPlan.Requests)
            {
                updateRequests.Add(request);
            }
        }

        if (lowerChordLinkedPartPlan.Enabled)
        {
            logWriter($"[LowerChord] TotalRequestsIncludingLinkedPart={updateRequests.Count}");
        }

        if (lowerSectionLinkedCompensationPlan.Enabled && lowerSectionLinkedCompensationPlan.Requests.Count > 0)
        {
            foreach (var request in lowerSectionLinkedCompensationPlan.Requests)
            {
                updateRequests.Add(request);
            }
        }

        if (lowerSection1530LinkedPartPlan.Enabled && lowerSection1530LinkedPartPlan.Requests.Count > 0)
        {
            foreach (var request in lowerSection1530LinkedPartPlan.Requests)
            {
                updateRequests.Add(request);
            }
        }

        var success = await _solidWorksService.UpdateDimensionsAsync(updateRequests, logWriter, cancellationToken);
        if (success)
        {
            var lowerChordLinkedPartExecutionResult = BuildLinkedLowerChordPartExecutionResult(
                lowerChordLinkedPartPlan,
                success);

            if (lowerTrussReportContext is not null)
            {
                lowerTrussReportContext = await RefreshLowerTrussAfterValuesAsync(
                    lowerTrussReportContext,
                    logWriter,
                    cancellationToken);
                assistantMessages.Add(BuildLowerTrussDetailedReport(lowerTrussReportContext, updateRequests));
            }

            if (lowerChordLinkedPartExecutionResult.Enabled)
            {
                assistantMessages.Add(
                    lowerChordLinkedPartExecutionResult switch
                    {
                        { PartFound: false } => "下弦杆已修改，但下弦杆联动零件未找到，未同步更新。",
                        { UpdateSucceeded: true, RequestsCreatedCount: 4 } => "已完成：桁架下弦杆 60mm x 60mm x 6mm，并已同步更新下弦杆联动零件。",
                        _ => "下弦杆已修改，但下弦杆联动零件尺寸更新失败，请查看日志。"
                    });
            }

            if (lowerSectionLinkedCompensationPlan.Enabled && !string.IsNullOrWhiteSpace(lowerSectionLinkedCompensationPlan.ResultMessage))
            {
                assistantMessages.Add(lowerSectionLinkedCompensationPlan.ResultMessage);
            }

            return new DimensionUpdateDispatchResult(
                true,
                false,
                !lowerChordLinkedPartExecutionResult.Enabled || !lowerChordLinkedPartExecutionResult.PartFound || lowerChordLinkedPartExecutionResult.UpdateSucceeded);
        }

        assistantMessages.Add("桁架构件尺寸更新失败，请检查高级调试日志。");
        return new DimensionUpdateDispatchResult(true, false, false);
    }

    private static bool IsMissingModelPathException(Exception exception)
    {
        return exception.Message.Contains("The path is empty", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("Parameter 'path'", StringComparison.OrdinalIgnoreCase);
    }

    private List<SolidWorksDimensionUpdateRequest> BuildDimensionUpdateRequests(
        IReadOnlyList<ParameterItem> parameters,
        Action<string> logWriter)
    {
        var requests = new List<SolidWorksDimensionUpdateRequest>();

        foreach (var parameter in parameters)
        {
            if (!IsExecutableSolidWorksParameter(parameter))
            {
                continue;
            }

            foreach (var target in parameter.SolidWorksTargets)
            {
                var partFilePath = ResolveWorkingPartPath(target, logWriter);
                if (string.IsNullOrWhiteSpace(partFilePath))
                {
                    continue;
                }

                requests.Add(new SolidWorksDimensionUpdateRequest
                {
                    ParameterName = parameter.Name,
                    DisplayName = string.IsNullOrWhiteSpace(parameter.DisplayName) ? parameter.Name : parameter.DisplayName,
                    PartFilePath = partFilePath,
                    Configuration = target.Configuration,
                    DimensionName = target.DimensionName,
                    Value = parameter.Value,
                    Unit = string.IsNullOrWhiteSpace(parameter.Unit) ? target.Unit : parameter.Unit
                });
            }
        }

        return requests;
    }

    private async Task AppendLinkedUpperChordRedTubeCompensationRequestsAsync(
        ICollection<SolidWorksDimensionUpdateRequest> requests,
        IReadOnlyList<EditableTrussMemberItem> members,
        TrussMemberCommandParseResult parseResult,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        var upperChordSectionChanged = parseResult.Width.HasValue &&
                                       parseResult.Height.HasValue &&
                                       members.Any(IsUpperChordMember);

        logWriter($"[LinkedRule] UpperChordSectionChanged={upperChordSectionChanged}");
        if (!upperChordSectionChanged)
        {
            return;
        }

        var rule = TrussLinkedParameterRules.UpperChordRedTubeLengthCompensation;
        var newUpperChordSectionMm = parseResult.Width!.Value;
        var linkedMember = members.FirstOrDefault(IsUpperChordMember) ?? members[0];
        await AppendLinkedUpperChordRedTubeCompensationRequestsCoreAsync(
            requests,
            linkedMember,
            newUpperChordSectionMm,
            logWriter,
            cancellationToken);
    }

    private async Task AppendLinkedUpperChordRedTubeCompensationRequestsAsync(
        ICollection<SolidWorksDimensionUpdateRequest> requests,
        IReadOnlyList<ParameterItem> parameters,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        if (!TryGetUpperChordSectionWidthFromParameters(parameters, out var newUpperChordSectionMm))
        {
            logWriter("[LinkedRule] UpperChordSectionChanged=False");
            return;
        }

        var linkedMember = _editableTrussMemberCatalogService.FindEnabledMemberById(TrussLinkedParameterRules.UpperChordMemberId);
        if (linkedMember is null)
        {
            logWriter("[LinkedRule] UpperChordSectionChanged=True");
            logWriter("[LinkedRule] Error=UpperChordMemberNotConfigured");
            return;
        }

        logWriter("[LinkedRule] UpperChordSectionChanged=True");
        await AppendLinkedUpperChordRedTubeCompensationRequestsCoreAsync(
            requests,
            linkedMember,
            newUpperChordSectionMm,
            logWriter,
            cancellationToken);
    }

    private async Task AppendLinkedUpperChordRedTubeCompensationRequestsCoreAsync(
        ICollection<SolidWorksDimensionUpdateRequest> requests,
        EditableTrussMemberItem linkedMember,
        decimal newUpperChordSectionMm,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        var rule = TrussLinkedParameterRules.UpperChordRedTubeLengthCompensation;
        var targetLengthMm = rule.BaseRedTubeExtrudeLengthMm +
                             (rule.BaseUpperChordSectionMm - newUpperChordSectionMm);

        logWriter($"[LinkedRule] Rule={rule.Name}");
        logWriter($"[LinkedRule] NewUpperChordSectionMm={newUpperChordSectionMm.ToString(CultureInfo.InvariantCulture)}");
        logWriter($"[LinkedRule] TargetRedTubeExtrudeLengthMm={targetLengthMm.ToString(CultureInfo.InvariantCulture)}");

        if (targetLengthMm <= 0)
        {
            logWriter("[LinkedRule] AppendRequest=False");
            logWriter("[LinkedRule] RedTubeExtrudeLengthUpdated=False");
            logWriter("[LinkedRule] Error=InvalidTargetLength");
            return;
        }

        var candidateParts = ResolveLinkedRulePartPaths(rule, logWriter);
        logWriter($"[LinkedRule] TargetPartMatched={candidateParts.Count > 0}");
        if (candidateParts.Count == 0)
        {
            logWriter("[LinkedRule] AppendRequest=False");
            logWriter("[LinkedRule] RedTubeExtrudeLengthUpdated=False");
            logWriter("[LinkedRule] Error=TargetPartNotFound");
            return;
        }

        var appendedCount = 0;
        var matchedDimensionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var partFilePath in candidateParts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filteredCandidates = BuildPartSpecificDimensionNameCandidates(
                partFilePath,
                rule.DimensionNameCandidates);
            var resolvedDimensionName = await ResolveLinkedRuleDimensionNameAsync(
                partFilePath,
                filteredCandidates,
                logWriter,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(resolvedDimensionName))
            {
                logWriter("[LinkedRule] TargetDimensionMatched=False");
                continue;
            }

            matchedDimensionNames.Add(resolvedDimensionName);
            requests.Add(new SolidWorksDimensionUpdateRequest
            {
                ParameterName = TrussLinkedParameterRules.LinkedRedTubeLengthParameterName,
                DisplayName = $"{linkedMember.Name}联动红色短管拉伸长度",
                PartFilePath = partFilePath,
                Configuration = string.Empty,
                DimensionName = resolvedDimensionName,
                Value = targetLengthMm.ToString(CultureInfo.InvariantCulture),
                Unit = "mm"
            });
            appendedCount++;
            logWriter("[LinkedRule] AppendRequest=True");
        }

        logWriter($"[LinkedRule] TargetDimensionMatched={matchedDimensionNames.Count > 0}");
        logWriter($"[LinkedRule] RedTubeExtrudeLengthUpdated={appendedCount > 0}");
        if (appendedCount == 0)
        {
            logWriter("[LinkedRule] AppendRequest=False");
            logWriter("[LinkedRule] Error=TargetDimensionNotFound");
        }
    }
    private async Task<List<SolidWorksDimensionUpdateRequest>> BuildTrussMemberDimensionUpdateRequestsWithPartMappingsAsync(
        IReadOnlyList<EditableTrussMemberItem> members,
        TrussMemberCommandParseResult parseResult,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        var requests = new List<SolidWorksDimensionUpdateRequest>();

        foreach (var member in members)
        {
            var hasUsablePartMappings = member.PartMappings.Any(mapping => IsUsablePartMapping(mapping, parseResult));
            if (!hasUsablePartMappings && HasLegacyTrussMemberMapping(member))
            {
                logWriter($"[TrussMapping] 构件 {member.Name} 使用顶层映射。");
                await AddTrussMemberRequestsFromEffectiveMappingAsync(
                    requests,
                    member,
                    ResolveConfiguredPartFilePathPortable(member.RelativePartPath, member.PartFilePath, logWriter),
                    null,
                    member.WidthDimensionName,
                    member.HeightDimensionName,
                    member.ThicknessDimensionName,
                    string.Empty,
                    string.Empty,
                    parseResult,
                    logWriter,
                    "TopLevel",
                    cancellationToken);
                continue;
            }

            var usableMappings = member.PartMappings
                .Select((mapping, index) => new { Mapping = mapping, Index = index })
                .Where(item => IsUsablePartMapping(item.Mapping, parseResult))
                .ToList();

            logWriter($"[TrussMapping] 构件 {member.Name} 使用 PartMappings 映射，命中 {usableMappings.Count} 条。");

            foreach (var item in member.PartMappings.Select((mapping, index) => new { Mapping = mapping, Index = index }))
            {
                if (!IsUsablePartMapping(item.Mapping, parseResult))
                {
                    logWriter($"[TrussMapping] 璺宠繃 {member.Name} 鐨?PartMapping[{item.Index}]锛屽師鍥狅細{BuildSkippedPartMappingReason(item.Mapping, parseResult)}");
                    continue;
                }

                await AddTrussMemberRequestsFromEffectiveMappingAsync(
                    requests,
                    member,
                    ResolveTrussMemberPartFilePath(member, item.Mapping, logWriter),
                    item.Mapping,
                    item.Mapping.WidthDimensionName,
                    item.Mapping.HeightDimensionName,
                    item.Mapping.ThicknessDimensionName,
                    item.Mapping.InnerWidthDimensionName,
                    item.Mapping.InnerHeightDimensionName,
                    parseResult,
                    logWriter,
                    $"PartMapping[{item.Index}]",
                    cancellationToken);
            }
        }

        return requests;
    }

    private async Task AddTrussMemberRequestsFromEffectiveMappingAsync(
        ICollection<SolidWorksDimensionUpdateRequest> requests,
        EditableTrussMemberItem member,
        string partFilePath,
        EditableTrussMemberPartMapping? partMapping,
        string widthDimensionName,
        string heightDimensionName,
        string thicknessDimensionName,
        string innerWidthDimensionName,
        string innerHeightDimensionName,
        TrussMemberCommandParseResult parseResult,
        Action<string> logWriter,
        string mappingSource,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(partFilePath))
        {
            logWriter($"[TrussMapping] 构件 {member.Name} 的 {mappingSource} 零件路径为空，跳过。");
            return;
        }

        LogLowerTrussLinkedPartDefinition(
            member,
            partFilePath,
            widthDimensionName,
            heightDimensionName,
            innerWidthDimensionName,
            innerHeightDimensionName,
            logWriter);

        if (parseResult.Width.HasValue)
        {
            if (string.IsNullOrWhiteSpace(widthDimensionName))
            {
                logWriter($"[TrussMapping] 构件 {member.Name} 的 {mappingSource} 缺少 WidthDimensionName，跳过宽度请求。");
            }
            else
            {
                var request = CreateTrussMemberUpdateRequest(
                    member,
                    partFilePath,
                    widthDimensionName,
                    "截面宽度",
                    parseResult.Width.Value,
                    parseResult.Unit);
                requests.Add(request);
                logWriter($"[TrussMapping] 鐢熸垚璇锋眰锛歅artFilePath={request.PartFilePath}, DimensionName={request.DimensionName}");
            }
        }

        if (parseResult.Height.HasValue)
        {
            if (string.IsNullOrWhiteSpace(heightDimensionName))
            {
                logWriter($"[TrussMapping] 构件 {member.Name} 的 {mappingSource} 缺少 HeightDimensionName，跳过高度请求。");
            }
            else
            {
                var request = CreateTrussMemberUpdateRequest(
                    member,
                    partFilePath,
                    heightDimensionName,
                    "閹搭亪娼版妯哄",
                    parseResult.Height.Value,
                    parseResult.Unit);
                requests.Add(request);
                logWriter($"[TrussMapping] 鐢熸垚璇锋眰锛歅artFilePath={request.PartFilePath}, DimensionName={request.DimensionName}");
            }
        }

        if (parseResult.Thickness.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(thicknessDimensionName))
            {
                var thicknessProbe = await _solidWorksService.ProbeDimensionAsync(
                    partFilePath,
                    thicknessDimensionName,
                    logWriter,
                    cancellationToken);

                if (thicknessProbe.Found)
                {
                    logWriter($"[TrussMapping] WallThicknessMappingScheme=DirectThicknessDimension; Member={member.Id}; Mapping={mappingSource}; DimensionName={thicknessDimensionName}");
                    if (string.Equals(member.Id, LowerTrussMemberId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(partFilePath, LockedLowerTrussModelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        logWriter("[LowerTruss] ThinFeatureThicknessUpdate=True");
                    }

                    var directThicknessRequest = CreateTrussMemberUpdateRequest(
                        member,
                        partFilePath,
                        thicknessDimensionName,
                        "澹佸帤",
                        parseResult.Thickness.Value,
                        parseResult.Unit);
                    requests.Add(directThicknessRequest);
                    logWriter($"[TrussMapping] Created request. PartFilePath={directThicknessRequest.PartFilePath}, DimensionName={directThicknessRequest.DimensionName}");
                    return;
                }

                logWriter($"[TrussMapping] Requested wall thickness dimension not found. PartFilePath={partFilePath}, RequestedDimension={thicknessDimensionName}");
            }
            else
            {
                logWriter($"[TrussMapping] Missing ThicknessDimensionName. Member={member.Id}, Mapping={mappingSource}. Trying inner cut fallback.");
            }

            var fallbackApplied = await TryAddDerivedInnerProfileRequestsAsync(
                requests,
                member,
                partFilePath,
                partMapping,
                widthDimensionName,
                heightDimensionName,
                innerWidthDimensionName,
                innerHeightDimensionName,
                parseResult,
                logWriter,
                mappingSource,
                cancellationToken);

            if (fallbackApplied)
            {
                logWriter($"[TrussMapping] WallThicknessMappingScheme=InnerCutDerived; Member={member.Id}; Mapping={mappingSource}");
                return;
            }

            throw new InvalidOperationException(
                $"Unable to resolve wall thickness mapping for {member.Id}. Part={partFilePath}, RequestedDimension={thicknessDimensionName}");
        }

    }

    private async Task<bool> TryAddDerivedInnerProfileRequestsAsync(
        ICollection<SolidWorksDimensionUpdateRequest> requests,
        EditableTrussMemberItem member,
        string partFilePath,
        EditableTrussMemberPartMapping? partMapping,
        string widthDimensionName,
        string heightDimensionName,
        string innerWidthDimensionName,
        string innerHeightDimensionName,
        TrussMemberCommandParseResult parseResult,
        Action<string> logWriter,
        string mappingSource,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(member.SectionType, "ClosedRectangularSection", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(innerWidthDimensionName) || string.IsNullOrWhiteSpace(innerHeightDimensionName))
        {
            logWriter($"[TrussMapping] Missing inner cut dimension names. Member={member.Id}, Mapping={mappingSource}");
            return false;
        }

        var outerWidth = parseResult.Width ?? await ProbeCurrentDimensionValueAsync(
            partFilePath,
            widthDimensionName,
            logWriter,
            cancellationToken);
        var outerHeight = parseResult.Height ?? await ProbeCurrentDimensionValueAsync(
            partFilePath,
            heightDimensionName,
            logWriter,
            cancellationToken);

        if (!outerWidth.HasValue || !outerHeight.HasValue || !parseResult.Thickness.HasValue)
        {
            logWriter($"[TrussMapping] Unable to derive inner cut dimensions. Member={member.Id}, Mapping={mappingSource}, outerWidth={outerWidth?.ToString(CultureInfo.InvariantCulture) ?? "null"}, outerHeight={outerHeight?.ToString(CultureInfo.InvariantCulture) ?? "null"}, wallThickness={parseResult.Thickness?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
            throw new InvalidOperationException("当前配置中没有找到该参数的固定映射，请检查参数配置文件。");
        }

        var wallThickness = parseResult.Thickness.Value;
        var innerWidth = ResolveDerivedInnerValue(
            partMapping?.InnerWidthValueMode,
            outerWidth.Value,
            wallThickness,
            "InnerWidth",
            logWriter);
        var innerHeight = ResolveDerivedInnerValue(
            partMapping?.InnerHeightValueMode,
            outerHeight.Value,
            wallThickness,
            "InnerHeight",
            logWriter);
        if (innerWidth <= 0m || innerHeight <= 0m)
        {
            throw new InvalidOperationException("壁厚数值过大，已取消修改。请确认截面尺寸和壁厚。");
        }

        LogLowerTrussLinkedPartValues(
            member,
            partFilePath,
            outerWidth.Value,
            outerHeight.Value,
            wallThickness,
            innerWidth,
            innerHeight,
            logWriter);

        var innerWidthProbe = await _solidWorksService.ProbeDimensionAsync(
            partFilePath,
            innerWidthDimensionName,
            logWriter,
            cancellationToken);
        var innerHeightProbe = await _solidWorksService.ProbeDimensionAsync(
            partFilePath,
            innerHeightDimensionName,
            logWriter,
            cancellationToken);

        if (!innerWidthProbe.Found || !innerHeightProbe.Found)
        {
            logWriter($"[TrussMapping] Fallback to inner cut dimensions failed. PartFilePath={partFilePath}, InnerWidthDimensionName={innerWidthDimensionName}, InnerHeightDimensionName={innerHeightDimensionName}");
            return false;
        }

        logWriter($"[TrussMapping] Fallback to inner cut dimensions. PartFilePath={partFilePath}");
        if (partMapping is not null)
        {
            logWriter($"[TrussMapping] InnerWidthValueMode={partMapping.InnerWidthValueMode}, InnerHeightValueMode={partMapping.InnerHeightValueMode}");
            logWriter($"[TrussMapping] InnerWidthOuterParameterKey={partMapping.InnerWidthOuterParameterKey}, InnerHeightOuterParameterKey={partMapping.InnerHeightOuterParameterKey}");
            logWriter($"[TrussMapping] InnerWidthThicknessParameterKey={partMapping.InnerWidthThicknessParameterKey}, InnerHeightThicknessParameterKey={partMapping.InnerHeightThicknessParameterKey}");
        }
        logWriter($"[TrussMapping] outerWidth={outerWidth.Value.ToString(CultureInfo.InvariantCulture)}, outerHeight={outerHeight.Value.ToString(CultureInfo.InvariantCulture)}, wallThickness={wallThickness.ToString(CultureInfo.InvariantCulture)}, innerWidth={innerWidth.ToString(CultureInfo.InvariantCulture)}, innerHeight={innerHeight.ToString(CultureInfo.InvariantCulture)}");
        logWriter($"[TrussMapping] Selected inner cut dimensions. InnerWidthDimensionName={innerWidthDimensionName}, InnerHeightDimensionName={innerHeightDimensionName}");

        var innerWidthRequest = CreateTrussMemberUpdateRequest(
            member,
            partFilePath,
            innerWidthDimensionName,
            "鍐呰厰瀹藉害",
            innerWidth,
            parseResult.Unit);
        requests.Add(innerWidthRequest);
        logWriter($"[TrussMapping] Created request. PartFilePath={innerWidthRequest.PartFilePath}, DimensionName={innerWidthRequest.DimensionName}");

        var innerHeightRequest = CreateTrussMemberUpdateRequest(
            member,
            partFilePath,
            innerHeightDimensionName,
            "鍐呰厰楂樺害",
            innerHeight,
            parseResult.Unit);
        requests.Add(innerHeightRequest);
        logWriter($"[TrussMapping] Created request. PartFilePath={innerHeightRequest.PartFilePath}, DimensionName={innerHeightRequest.DimensionName}");

        return true;
    }

    private static decimal ResolveDerivedInnerValue(
        string? valueMode,
        decimal outerValue,
        decimal wallThickness,
        string label,
        Action<string> logWriter)
    {
        var effectiveMode = string.IsNullOrWhiteSpace(valueMode)
            ? ValueModeOuterMinusTwoThickness
            : valueMode.Trim();

        if (string.Equals(effectiveMode, ValueModeOuterMinusTwoThickness, StringComparison.OrdinalIgnoreCase))
        {
            return outerValue - (2m * wallThickness);
        }

        logWriter($"[TrussMapping] Unsupported derived value mode for {label}: {effectiveMode}");
        throw new InvalidOperationException("当前配置中没有找到该参数的固定映射，请检查参数配置文件。");
    }

    private async Task<decimal?> ProbeCurrentDimensionValueAsync(
        string partFilePath,
        string dimensionName,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dimensionName))
        {
            return null;
        }

        var probeResult = await _solidWorksService.ProbeDimensionAsync(
            partFilePath,
            dimensionName,
            logWriter,
            cancellationToken);
        return probeResult.Found ? probeResult.CurrentValue : null;
    }

    private async Task<LowerTrussReportContext?> TryCreateLowerTrussReportContextAsync(
        IReadOnlyList<EditableTrussMemberItem> members,
        TrussMemberCommandParseResult parseResult,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        var lowerTrussMember = members.FirstOrDefault(member =>
            string.Equals(member.Id, LowerTrussMemberId, StringComparison.OrdinalIgnoreCase));
        if (lowerTrussMember is null)
        {
            return null;
        }

        var mapping = lowerTrussMember.PartMappings.FirstOrDefault();
        if (mapping is null)
        {
            throw new InvalidOperationException("下桁架未配置可执行的 PartMapping。");
        }

        var configuredPath = mapping.PartFilePath?.Trim() ?? string.Empty;
        if (!string.Equals(configuredPath, LockedLowerTrussModelPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"下桁架模型路径配置错误。必须严格等于：{LockedLowerTrussModelPath}");
        }

        if (!File.Exists(configuredPath))
        {
            throw new FileNotFoundException($"下桁架模型不存在：{configuredPath}", configuredPath);
        }

        var modelPath = configuredPath;

        var outer = parseResult.Width ?? parseResult.Height;
        var thickness = parseResult.Thickness;
        if (!outer.HasValue || !thickness.HasValue)
        {
            throw new InvalidOperationException("下桁架修改必须提供完整规格 A×A×T。");
        }

        var inner = ResolveDerivedInnerValue(
            mapping.InnerWidthValueMode,
            outer.Value,
            thickness.Value,
            "LowerTrussInnerWidth",
            logWriter);
        if (inner <= 0m)
        {
            throw new InvalidOperationException("下桁架壁厚数值过大，内边长计算结果无效。");
        }

        var dimensions = new List<LowerTrussDimensionReportItem>();
        foreach (var dimensionName in new[]
                 {
                     mapping.WidthDimensionName,
                     mapping.HeightDimensionName,
                     mapping.InnerWidthDimensionName,
                     mapping.InnerHeightDimensionName
                 })
        {
            if (string.IsNullOrWhiteSpace(dimensionName))
            {
                throw new InvalidOperationException("下桁架四个目标尺寸配置不完整。");
            }

            var probe = await _solidWorksService.ProbeDimensionAsync(
                modelPath,
                dimensionName,
                logWriter,
                cancellationToken);
            if (!probe.Found)
            {
                throw new InvalidOperationException($"未找到下桁架目标尺寸：{dimensionName}");
            }

            dimensions.Add(new LowerTrussDimensionReportItem(
                string.IsNullOrWhiteSpace(probe.ResolvedDimensionName) ? dimensionName : probe.ResolvedDimensionName,
                probe.CurrentValue,
                null));
        }

        return new LowerTrussReportContext(
            lowerTrussMember.Name,
            modelPath,
            outer.Value,
            thickness.Value,
            inner,
            dimensions,
            string.Empty);
    }

    private async Task<LowerTrussReportContext> RefreshLowerTrussAfterValuesAsync(
        LowerTrussReportContext context,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        var refreshedDimensions = new List<LowerTrussDimensionReportItem>(context.Dimensions.Count);
        foreach (var item in context.Dimensions)
        {
            var probe = await _solidWorksService.ProbeDimensionAsync(
                context.ModelPath,
                item.DimensionName,
                logWriter,
                cancellationToken);

            refreshedDimensions.Add(item with
            {
                AfterValue = probe.Found ? probe.CurrentValue : null
            });
        }

        return context with
        {
            Dimensions = refreshedDimensions
        };
    }

    private static LowerTrussBackupResult TryBackupLowerTrussModel(string modelPath, Action<string> logWriter)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                return new LowerTrussBackupResult(false, "下桁架模型不存在，已停止修改。", string.Empty);
            }

            var directory = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return new LowerTrussBackupResult(false, "下桁架模型目录无效，已停止修改。", string.Empty);
            }

            var backupDirectory = Path.Combine(directory, "backup");
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(
                backupDirectory,
                $"{Path.GetFileNameWithoutExtension(modelPath)}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(modelPath)}");
            File.Copy(modelPath, backupPath, overwrite: false);
            logWriter($"[LowerTruss] BackupPath={backupPath}");
            return new LowerTrussBackupResult(true, string.Empty, backupPath);
        }
        catch (Exception ex)
        {
            logWriter($"[LowerTruss] BackupFailed={ex}");
            return new LowerTrussBackupResult(false, $"无法备份下桁架原模型，已停止修改。原因：{ex.Message}", string.Empty);
        }
    }

    private static string BuildLowerTrussDetailedReport(
        LowerTrussReportContext context,
        IReadOnlyList<SolidWorksDimensionUpdateRequest> requests)
    {
        var builder = new StringBuilder();
        builder.AppendLine("已完成下桁架模型修改。");
        builder.AppendLine();
        builder.AppendLine("模型路径：");
        builder.AppendLine(context.ModelPath);
        builder.AppendLine();
        builder.AppendLine("备份路径：");
        builder.AppendLine(string.IsNullOrWhiteSpace(context.BackupPath) ? "未记录" : context.BackupPath);
        builder.AppendLine();
        builder.AppendLine($"识别到的构件名称：{context.MemberName}");
        builder.AppendLine($"目标规格：{context.OuterSize:0.##}×{context.OuterSize:0.##}×{context.Thickness:0.##}");
        builder.AppendLine();
        builder.AppendLine("计算：");
        builder.AppendLine($"外边长 A = {context.OuterSize:0.##}");
        builder.AppendLine($"壁厚 T = {context.Thickness:0.##}");
        builder.AppendLine($"内边长 B = {context.OuterSize:0.##} - 2×{context.Thickness:0.##} = {context.InnerSize:0.##}");
        builder.AppendLine();
        builder.AppendLine("已修改尺寸：");

        foreach (var item in context.Dimensions)
        {
            builder.AppendLine(
                $"{item.DimensionName}：{FormatMillimeterValue(item.BeforeValue)} -> {FormatMillimeterValue(item.AfterValue, allowUnknown: true)}");
        }

        builder.AppendLine();
        builder.AppendLine("重建/保存：已调用 SolidWorks 重建与保存流程，UpdateDimensionsAsync 未抛异常。");
        builder.AppendLine("修改范围：仅修改下桁架绑定模型中的上述四个尺寸。");
        builder.Append("是否发现其他受影响内容：未发现。");
        return builder.ToString().TrimEnd();
    }

    private async Task<LinkedLowerChordPartPlan> BuildLinkedLowerChordPartPlanAsync(
        IReadOnlyList<EditableTrussMemberItem> members,
        TrussMemberCommandParseResult parseResult,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        var isLowerChordModification = members.Any(member =>
            member.Id.StartsWith("lower_chord", StringComparison.OrdinalIgnoreCase));
        if (!isLowerChordModification)
        {
            return LinkedLowerChordPartPlan.Disabled;
        }

        var linkedPartPath = ResolveLinkedLowerChordPartPath(logWriter);
        logWriter("[LinkedLowerChordPart] Enabled=True");
        logWriter("[VersionMarker] LinkedLowerChordPartPathResolver v5 loaded");
        logWriter("[LinkedLowerChordPart] ResolverVersion=ExactRealFileNameV5");
        logWriter("[LinkedLowerChordPart] Trigger=LowerChordModification");
        logWriter($"[LinkedPart] TargetPath={linkedPartPath}");
        logWriter($"[LinkedPart] Exists={(!string.IsNullOrWhiteSpace(linkedPartPath) && File.Exists(linkedPartPath))}");
        logWriter($"[LinkedPart] RequestedSpec={BuildLinkedPartRequestedSpec(parseResult)}");
        logWriter($"[LinkedPart] ModifySpec={BuildLinkedPartRequestedSpec(parseResult)}");

        if (string.IsNullOrWhiteSpace(linkedPartPath) || !File.Exists(linkedPartPath))
        {
            throw new InvalidOperationException("已中止：下弦杆联动零件不存在，未执行修改。");
        }

        logWriter($"[LinkedPart] BeforeLastWriteTime={File.GetLastWriteTime(linkedPartPath):O}");

        decimal? outerWidth = parseResult.Width;
        decimal? outerHeight = parseResult.Height;
        decimal? thickness = parseResult.Thickness;

        var requests = new List<SolidWorksDimensionUpdateRequest>();

        if (!outerWidth.HasValue)
        {
            outerWidth = await ProbeLinkedLowerChordDimensionValueAsync(
                linkedPartPath,
                LinkedLowerChordOuterWidthDimension,
                "D3",
                logWriter,
                cancellationToken);
        }

        if (!outerHeight.HasValue)
        {
            outerHeight = await ProbeLinkedLowerChordDimensionValueAsync(
                linkedPartPath,
                LinkedLowerChordOuterHeightDimension,
                "D1",
                logWriter,
                cancellationToken);
        }

        if (outerWidth.HasValue)
        {
            logWriter($"[LinkedLowerChordPart] OuterWidth={outerWidth.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (outerHeight.HasValue)
        {
            logWriter($"[LinkedLowerChordPart] OuterHeight={outerHeight.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (thickness.HasValue)
        {
            logWriter($"[LinkedLowerChordPart] Thickness={thickness.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        logWriter("[LinkedPart] Applying linked lower chord update");

        if (parseResult.Width.HasValue)
        {
            AddLinkedLowerChordPartRequest(
                requests,
                linkedPartPath,
                LinkedLowerChordOuterWidthDimension,
                parseResult.Width.Value,
                logWriter);
        }

        if (parseResult.Height.HasValue)
        {
            AddLinkedLowerChordPartRequest(
                requests,
                linkedPartPath,
                LinkedLowerChordOuterHeightDimension,
                parseResult.Height.Value,
                logWriter);
        }

        if (!thickness.HasValue)
        {
            logWriter("[LinkedLowerChordPart] SkipInnerSizeUpdate Reason=ThicknessNotProvided");
            logWriter($"[LinkedLowerChordPart] CreatedRequestsCount={requests.Count}");
            logWriter($"[LowerChord] TotalRequestsIncludingLinkedPart={9 + requests.Count}");
            return new LinkedLowerChordPartPlan(true, true, linkedPartPath, requests);
        }

        if (!outerWidth.HasValue || !outerHeight.HasValue)
        {
            logWriter("[LinkedLowerChordPart] Skip Reason=OuterSizeUnknownForThicknessOnly");
            logWriter($"[LinkedLowerChordPart] CreatedRequestsCount={requests.Count}");
            logWriter($"[LowerChord] TotalRequestsIncludingLinkedPart={9 + requests.Count}");
            return new LinkedLowerChordPartPlan(true, true, linkedPartPath, requests);
        }

        var innerWidth = outerWidth.Value - (2m * thickness.Value);
        var innerHeight = outerHeight.Value - (2m * thickness.Value);
        if (innerWidth <= 0m || innerHeight <= 0m)
        {
            throw new InvalidOperationException("下弦杆联动零件的内尺寸计算结果无效，请确认截面尺寸和壁厚。");
        }

        logWriter($"[LinkedLowerChordPart] InnerWidth={innerWidth.ToString(CultureInfo.InvariantCulture)}");
        logWriter($"[LinkedLowerChordPart] InnerHeight={innerHeight.ToString(CultureInfo.InvariantCulture)}");

        AddLinkedLowerChordPartRequest(
            requests,
            linkedPartPath,
            LinkedLowerChordInnerWidthDimension,
            innerWidth,
            logWriter);
        AddLinkedLowerChordPartRequest(
            requests,
            linkedPartPath,
            LinkedLowerChordInnerHeightDimension,
            innerHeight,
            logWriter);

        logWriter($"[LinkedLowerChordPart] CreatedRequestsCount={requests.Count}");
        logWriter($"[LowerChord] TotalRequestsIncludingLinkedPart={9 + requests.Count}");
        return new LinkedLowerChordPartPlan(true, true, linkedPartPath, requests);
    }

    private static void AddLinkedLowerChordPartRequest(
        ICollection<SolidWorksDimensionUpdateRequest> requests,
        string partFilePath,
        string dimensionName,
        decimal value,
        Action<string> logWriter)
    {
        requests.Add(new SolidWorksDimensionUpdateRequest
        {
            ParameterName = $"linked_lower_chord_part.{dimensionName}",
            DisplayName = $"下弦杆联动零件{TrimPartSuffix(dimensionName)}",
            PartFilePath = partFilePath,
            Configuration = string.Empty,
            DimensionName = dimensionName,
            Value = value.ToString(CultureInfo.InvariantCulture),
            Unit = "mm"
        });

        logWriter($"[LinkedLowerChordPart] Created request. DimensionName={dimensionName}, Value={value.ToString(CultureInfo.InvariantCulture)}");
    }

    private async Task<LowerSectionLinkedCompensationPlan> BuildLowerSectionLinkedCompensationPlanAsync(
        IReadOnlyList<EditableTrussMemberItem> members,
        TrussMemberCommandParseResult parseResult,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        var linkedMember = members.FirstOrDefault(IsLowerSectionLinkedCompensationMember);
        var sectionChanged = linkedMember is not null &&
                             parseResult.Width.HasValue &&
                             parseResult.Height.HasValue;
        if (!sectionChanged)
        {
            return LowerSectionLinkedCompensationPlan.Disabled;
        }

        var targetWidth = parseResult.Width!.Value;
        var targetHeight = parseResult.Height!.Value;
        var thicknessText = parseResult.Thickness?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
        var targetSectionText = $"{targetWidth:0.##}x{targetHeight:0.##}x{thicknessText}";
        var rule = TrussLinkedParameterRules.LowerSectionLinkedPartCompensation;

        logWriter("[LinkedPartCompensation] LowerTruss linked compensation enabled");
        logWriter($"[LinkedPartCompensation] TriggerMember={linkedMember!.Id}");
        logWriter($"[LinkedPartCompensation] TargetSection={targetSectionText}");

        if (targetWidth > rule.BaseUpperChordSectionMm)
        {
            var rejectedMessage = $"下桁架/下弦杆已修改，但关联零件补偿未执行：目标宽度 {targetWidth:0.##}mm 大于 70mm，当前仅支持 70mm 及以下的补偿规则。";
            logWriter($"[LinkedPartCompensation] AppendRequest=False");
            logWriter($"[LinkedPartCompensation] Error=TargetWidthExceedsSupportedBase; Width={targetWidth.ToString(CultureInfo.InvariantCulture)}");
            return new LowerSectionLinkedCompensationPlan(true, false, false, string.Empty, rejectedMessage, Array.Empty<SolidWorksDimensionUpdateRequest>());
        }

        var candidateParts = ResolveLinkedRulePartPaths(rule, logWriter);
        var selectedPartPath = SelectPreferredLowerSectionLinkedCompensationPartPath(candidateParts);
        if (string.IsNullOrWhiteSpace(selectedPartPath))
        {
            logWriter("[LinkedPartCompensation] AppendRequest=False");
            logWriter("[LinkedPartCompensation] Error=TargetPartNotFound");
            return new LowerSectionLinkedCompensationPlan(
                true,
                false,
                false,
                string.Empty,
                "下桁架/下弦杆已修改，但未找到关联补偿零件 20-下-方管70×70×5-1280，未同步更新。",
                Array.Empty<SolidWorksDimensionUpdateRequest>());
        }

        logWriter($"[LinkedPartCompensation] Path={selectedPartPath}");

        var widthDimensionName = await ResolveLinkedRuleDimensionNameAsync(
            selectedPartPath,
            BuildPartSpecificDimensionNameCandidates(
                selectedPartPath,
                [
                    LowerSectionLinkedCompensationWidthDimension,
                    "D1@草图1",
                    "D1"
                ]),
            logWriter,
            cancellationToken);
        var heightDimensionName = await ResolveLinkedRuleDimensionNameAsync(
            selectedPartPath,
            BuildPartSpecificDimensionNameCandidates(
                selectedPartPath,
                [
                    LowerSectionLinkedCompensationHeightDimension,
                    "D2@草图1",
                    "D2",
                    "D20@草图1",
                    "D20"
                ]),
            logWriter,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(widthDimensionName) ||
            string.IsNullOrWhiteSpace(heightDimensionName))
        {
            logWriter("[LinkedPartCompensation] AppendRequest=False");
            logWriter($"[LinkedPartCompensation] WidthDimensionResolved={!string.IsNullOrWhiteSpace(widthDimensionName)}");
            logWriter($"[LinkedPartCompensation] HeightDimensionResolved={!string.IsNullOrWhiteSpace(heightDimensionName)}");
            return new LowerSectionLinkedCompensationPlan(
                true,
                true,
                false,
                selectedPartPath,
                "下桁架/下弦杆已修改，但关联补偿零件的目标尺寸名未全部解析成功，请查看日志。",
                Array.Empty<SolidWorksDimensionUpdateRequest>());
        }

        var requests = new List<SolidWorksDimensionUpdateRequest>();
        AddLowerSectionLinkedCompensationRequest(
            requests,
            selectedPartPath,
            widthDimensionName,
            "关联零件截面宽度",
            "linked_lower_section_compensation.width",
            targetWidth,
            logWriter);
        logWriter($"[LinkedPartCompensation] Set D1@草图1={targetWidth:0.##}mm");
        AddLowerSectionLinkedCompensationRequest(
            requests,
            selectedPartPath,
            heightDimensionName,
            "关联零件截面高度",
            "linked_lower_section_compensation.height",
            targetHeight,
            logWriter);
        logWriter($"[LinkedPartCompensation] Set D20@草图1={targetHeight:0.##}mm");

        return new LowerSectionLinkedCompensationPlan(true, true, true, selectedPartPath, string.Empty, requests);
    }

    private static void AddLowerSectionLinkedCompensationRequest(
        ICollection<SolidWorksDimensionUpdateRequest> requests,
        string partFilePath,
        string dimensionName,
        string displayName,
        string parameterName,
        decimal value,
        Action<string> logWriter)
    {
        requests.Add(new SolidWorksDimensionUpdateRequest
        {
            ParameterName = parameterName,
            DisplayName = displayName,
            PartFilePath = partFilePath,
            Configuration = string.Empty,
            DimensionName = dimensionName,
            Value = value.ToString(CultureInfo.InvariantCulture),
            Unit = "mm"
        });

        logWriter($"[LinkedPartCompensation] Created request. PartFilePath={partFilePath}, DimensionName={dimensionName}, Value={value.ToString(CultureInfo.InvariantCulture)}");
    }

    private async Task<LinkedLowerTrussSectionPartPlan> BuildLowerTrussSection1530LinkedPartPlanAsync(
        IReadOnlyCollection<SolidWorksDimensionUpdateRequest> existingRequests,
        IReadOnlyList<EditableTrussMemberItem> members,
        TrussMemberCommandParseResult parseResult,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        var isLowerSectionModification = members.Any(IsLowerSectionLinkedCompensationMember);
        if (!isLowerSectionModification)
        {
            return LinkedLowerTrussSectionPartPlan.Disabled;
        }

        if (!parseResult.Width.HasValue || !parseResult.Height.HasValue || !parseResult.Thickness.HasValue)
        {
            logWriter("[Linked1530Part] Skip Reason=IncompleteSectionSpec");
            logWriter($"[Linked1530Part] RequestedSpec={BuildLinkedPartRequestedSpec(parseResult)}");
            return LinkedLowerTrussSectionPartPlan.Disabled;
        }

        if (existingRequests.Any(request =>
                string.Equals(request.PartFilePath, LinkedLowerTrussSectionModelPath, StringComparison.OrdinalIgnoreCase)))
        {
            logWriter("[Linked1530Part] Skip Reason=RequestsAlreadyGenerated");
            logWriter($"[Linked1530Part] TargetPath={LinkedLowerTrussSectionModelPath}");
            return LinkedLowerTrussSectionPartPlan.Disabled;
        }

        logWriter("[Linked1530Part] Enabled=True");
        logWriter("[Linked1530Part] Trigger=LowerChordOrLowerTrussSectionModification");
        logWriter($"[Linked1530Part] TargetPath={LinkedLowerTrussSectionModelPath}");
        logWriter($"[Linked1530Part] Exists={File.Exists(LinkedLowerTrussSectionModelPath)}");
        logWriter($"[Linked1530Part] RequestedSpec={BuildLinkedPartRequestedSpec(parseResult)}");

        if (!File.Exists(LinkedLowerTrussSectionModelPath))
        {
            logWriter("[Linked1530Part] Skip Reason=TargetPartNotFound");
            return LinkedLowerTrussSectionPartPlan.Disabled;
        }

        var widthDimensionName = await ResolveLinkedRuleDimensionNameAsync(
            LinkedLowerTrussSectionModelPath,
            BuildPartSpecificDimensionNameCandidates(
                LinkedLowerTrussSectionModelPath,
                [
                    LinkedLowerTrussSectionWidthDimension,
                    "D1@草图1",
                    "D1"
                ]),
            logWriter,
            cancellationToken);
        var heightDimensionName = await ResolveLinkedRuleDimensionNameAsync(
            LinkedLowerTrussSectionModelPath,
            BuildPartSpecificDimensionNameCandidates(
                LinkedLowerTrussSectionModelPath,
                [
                    LinkedLowerTrussSectionHeightDimension,
                    "D2@草图1",
                    "D2"
                ]),
            logWriter,
            cancellationToken);
        var thicknessDimensionName = await ResolveLinkedRuleDimensionNameAsync(
            LinkedLowerTrussSectionModelPath,
            BuildPartSpecificDimensionNameCandidates(
                LinkedLowerTrussSectionModelPath,
                [
                    LinkedLowerTrussSectionThicknessDimension,
                    "T1@拉伸-薄壁1",
                    "T1"
                ]),
            logWriter,
            cancellationToken);

        logWriter($"[Linked1530Part] WidthDimensionResolved={!string.IsNullOrWhiteSpace(widthDimensionName)}");
        logWriter($"[Linked1530Part] HeightDimensionResolved={!string.IsNullOrWhiteSpace(heightDimensionName)}");
        logWriter($"[Linked1530Part] ThicknessDimensionResolved={!string.IsNullOrWhiteSpace(thicknessDimensionName)}");

        if (string.IsNullOrWhiteSpace(thicknessDimensionName))
        {
            logWriter($"[Linked1530Part] ThicknessDimensionExpected={LinkedLowerTrussSectionThicknessDimension}");
            logWriter("[Linked1530Part] Skip Reason=ThicknessDimensionNotConfirmed");
            return LinkedLowerTrussSectionPartPlan.Disabled;
        }

        if (string.IsNullOrWhiteSpace(widthDimensionName) || string.IsNullOrWhiteSpace(heightDimensionName))
        {
            logWriter("[Linked1530Part] Skip Reason=SectionDimensionNotConfirmed");
            return LinkedLowerTrussSectionPartPlan.Disabled;
        }

        var requests = new List<SolidWorksDimensionUpdateRequest>();
        AddLinkedLowerTrussSection1530Request(
            requests,
            LinkedLowerTrussSectionModelPath,
            widthDimensionName,
            "linked_lower_truss_section_1530.width",
            "下部联动零件1530截面宽度",
            parseResult.Width.Value,
            logWriter);
        AddLinkedLowerTrussSection1530Request(
            requests,
            LinkedLowerTrussSectionModelPath,
            heightDimensionName,
            "linked_lower_truss_section_1530.height",
            "下部联动零件1530截面高度",
            parseResult.Height.Value,
            logWriter);
        AddLinkedLowerTrussSection1530Request(
            requests,
            LinkedLowerTrussSectionModelPath,
            thicknessDimensionName,
            "linked_lower_truss_section_1530.thickness",
            "下部联动零件1530壁厚",
            parseResult.Thickness.Value,
            logWriter);

        logWriter($"[Linked1530Part] {Path.GetFileName(LinkedLowerTrussSectionModelPath)}: {TrimPartSuffix(widthDimensionName)} = {parseResult.Width.Value.ToString(CultureInfo.InvariantCulture)}");
        logWriter($"[Linked1530Part] {Path.GetFileName(LinkedLowerTrussSectionModelPath)}: {TrimPartSuffix(heightDimensionName)} = {parseResult.Height.Value.ToString(CultureInfo.InvariantCulture)}");
        logWriter($"[Linked1530Part] {Path.GetFileName(LinkedLowerTrussSectionModelPath)}: {TrimPartSuffix(thicknessDimensionName)} = {parseResult.Thickness.Value.ToString(CultureInfo.InvariantCulture)}");
        logWriter($"[Linked1530Part] CreatedRequestsCount={requests.Count}");

        return new LinkedLowerTrussSectionPartPlan(true, true, LinkedLowerTrussSectionModelPath, requests);
    }

    private static void AddLinkedLowerTrussSection1530Request(
        ICollection<SolidWorksDimensionUpdateRequest> requests,
        string partFilePath,
        string dimensionName,
        string parameterName,
        string displayName,
        decimal value,
        Action<string> logWriter)
    {
        requests.Add(new SolidWorksDimensionUpdateRequest
        {
            ParameterName = parameterName,
            DisplayName = displayName,
            PartFilePath = partFilePath,
            Configuration = string.Empty,
            DimensionName = dimensionName,
            Value = value.ToString(CultureInfo.InvariantCulture),
            Unit = "mm"
        });

        logWriter($"[Linked1530Part] Created request. PartFilePath={partFilePath}, DimensionName={dimensionName}, Value={value.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string BuildLinkedPartRequestedSpec(TrussMemberCommandParseResult parseResult)
    {
        var width = parseResult.Width?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
        var height = parseResult.Height?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
        var thickness = parseResult.Thickness?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
        return $"{width}x{height}x{thickness}";
    }

    private static LinkedLowerChordPartExecutionResult BuildLinkedLowerChordPartExecutionResult(
        LinkedLowerChordPartPlan plan,
        bool mainUpdateSucceeded)
    {
        if (!plan.Enabled)
        {
            return LinkedLowerChordPartExecutionResult.Disabled;
        }

        if (!plan.PartFound)
        {
            return new LinkedLowerChordPartExecutionResult(true, false, false, plan.PartPath, plan.Requests.Count);
        }

        return new LinkedLowerChordPartExecutionResult(true, true, mainUpdateSucceeded, plan.PartPath, plan.Requests.Count);
    }

    private async Task<decimal?> ProbeLinkedLowerChordDimensionValueAsync(
        string partFilePath,
        string fullDimensionName,
        string baseName,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in BuildLinkedLowerChordDimensionCandidates(fullDimensionName, baseName))
        {
            var probe = await _solidWorksService.ProbeDimensionAsync(
                partFilePath,
                candidate,
                logWriter,
                cancellationToken);
            if (probe.Found)
            {
                return probe.CurrentValue;
            }
        }

        return null;
    }

    private string ResolveLinkedLowerChordPartPath(Action<string> logWriter)
    {
        const string fixedRelativePath = @"桁架2\1-方管70×70×5-1159.SLDPRT";
        var workingModelPath = string.IsNullOrWhiteSpace(_workspaceManager.WorkingModelPath)
            ? _workspaceManager.WorkingModelFolder
            : _workspaceManager.WorkingModelPath;

        logWriter($"[LinkedPart] WorkingModelPath={workingModelPath}");
        logWriter($"[LinkedPart] FixedRelativePath={fixedRelativePath}");

        if (string.IsNullOrWhiteSpace(workingModelPath))
        {
            logWriter("[LinkedPart] FixedFullPath=");
            return string.Empty;
        }

        var fixedFullPath = Path.GetFullPath(Path.Combine(
            workingModelPath,
            fixedRelativePath));

        logWriter($"[LinkedPart] FixedFullPath={fixedFullPath}");
        return fixedFullPath;
    }

    private string TryResolveLinkedLowerChordNewModelFolder()
    {
        var candidates = new[]
        {
            _workspaceManager.WorkingModelFolder,
            _workspaceManager.CurrentAssemblyPath,
            _workspaceManager.WorkingModelPath
        };

        foreach (var candidate in candidates)
        {
            var folder = ResolveContainingNewModelFolder(candidate);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                return folder;
            }
        }

        return string.Empty;
    }

    private IEnumerable<string> BuildLinkedLowerChordSearchRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfExists(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalized = Path.GetFullPath(path);
            roots.Add(normalized);
        }

        AddIfExists(Path.GetDirectoryName(_workspaceManager.CurrentAssemblyPath));
        AddIfExists(_workspaceManager.WorkingModelFolder);
        AddIfExists(Directory.GetParent(_workspaceManager.WorkingModelFolder)?.FullName);

        var solidworksModelFolder = ResolveContainingFolderSegment(_workspaceManager.WorkingModelFolder, "Solidworks模型");
        if (!string.IsNullOrWhiteSpace(solidworksModelFolder))
        {
            AddIfExists(solidworksModelFolder);
        }

        return roots;
    }

    private static string ResolveContainingNewModelFolder(string? path)
    {
        return ResolveContainingFolderSegment(path, "新模型");
    }

    private static string ResolveContainingFolderSegment(string? path, string segment)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var fullPath = Path.GetFullPath(path);
        var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < parts.Length; i++)
        {
            if (!string.Equals(parts[i], segment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return string.Join(Path.DirectorySeparatorChar, parts.Take(i + 1));
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> BuildLinkedLowerChordDimensionCandidates(string fullDimensionName, string baseName)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                candidates.Add(value);
            }
        }

        Add(fullDimensionName);
        Add($"{baseName}@草图2");
        Add(baseName);
        return candidates;
    }

    private string ResolveTrussMemberPartFilePath(
        EditableTrussMemberItem member,
        EditableTrussMemberPartMapping mapping,
        Action<string> logWriter)
    {
        if (!string.Equals(member.Id, LowerTrussMemberId, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveConfiguredPartFilePathPortable(mapping.RelativePartPath, mapping.PartFilePath, logWriter);
        }

        var configuredPath = mapping.PartFilePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("下桁架 PartMapping 缺少固定零件路径配置。");
        }

        if (string.Equals(configuredPath, LockedLowerTrussModelPath, StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(configuredPath))
        {
            throw new FileNotFoundException($"下桁架模型不存在：{configuredPath}", configuredPath);
        }

        if (string.Equals(configuredPath, LinkedLowerTrussModelPath, StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(configuredPath))
        {
            throw new FileNotFoundException($"下桁架联动零件不存在：{configuredPath}", configuredPath);
        }

        if (string.Equals(configuredPath, LinkedLowerTrussSectionModelPath, StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(configuredPath))
        {
            throw new FileNotFoundException($"下桁架联动零件不存在：{configuredPath}", configuredPath);
        }

        if (!string.Equals(configuredPath, LockedLowerTrussModelPath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuredPath, LinkedLowerTrussModelPath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuredPath, LinkedLowerTrussSectionModelPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"下桁架 PartMapping 路径未在允许列表中：{configuredPath}");
        }

        return configuredPath;
    }

    private static void LogLowerTrussLinkedPartDefinition(
        EditableTrussMemberItem member,
        string partFilePath,
        string widthDimensionName,
        string heightDimensionName,
        string innerWidthDimensionName,
        string innerHeightDimensionName,
        Action<string> logWriter)
    {
        if (!string.Equals(member.Id, LowerTrussMemberId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(partFilePath, LinkedLowerTrussModelPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        logWriter("[LinkedLowerTrussPart] Enabled=True");
        logWriter($"[LinkedLowerTrussPart] PartPath={partFilePath}");
        logWriter($"[LinkedLowerTrussPart] OuterWidthDimension={TrimPartSuffix(widthDimensionName)}");
        logWriter($"[LinkedLowerTrussPart] OuterHeightDimension={TrimPartSuffix(heightDimensionName)}");
        logWriter($"[LinkedLowerTrussPart] InnerWidthDimension={TrimPartSuffix(innerWidthDimensionName)}");
        logWriter($"[LinkedLowerTrussPart] InnerHeightDimension={TrimPartSuffix(innerHeightDimensionName)}");
    }

    private static void LogLowerTrussLinkedPartValues(
        EditableTrussMemberItem member,
        string partFilePath,
        decimal outerWidth,
        decimal outerHeight,
        decimal thickness,
        decimal innerWidth,
        decimal innerHeight,
        Action<string> logWriter)
    {
        if (!string.Equals(member.Id, LowerTrussMemberId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(partFilePath, LinkedLowerTrussModelPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        logWriter($"[LinkedLowerTrussPart] OuterWidth={outerWidth.ToString(CultureInfo.InvariantCulture)}");
        logWriter($"[LinkedLowerTrussPart] OuterHeight={outerHeight.ToString(CultureInfo.InvariantCulture)}");
        logWriter($"[LinkedLowerTrussPart] Thickness={thickness.ToString(CultureInfo.InvariantCulture)}");
        logWriter($"[LinkedLowerTrussPart] InnerWidth={innerWidth.ToString(CultureInfo.InvariantCulture)}");
        logWriter($"[LinkedLowerTrussPart] InnerHeight={innerHeight.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string TrimPartSuffix(string dimensionName)
    {
        if (string.IsNullOrWhiteSpace(dimensionName))
        {
            return string.Empty;
        }

        var lastAt = dimensionName.LastIndexOf('@');
        return lastAt > 0 ? dimensionName[..lastAt] : dimensionName;
    }

    private static string FormatMillimeterValue(decimal? value, bool allowUnknown = false)
    {
        if (value.HasValue)
        {
            return $"{value.Value:0.##}";
        }

        return allowUnknown ? "读取失败" : "未知";
    }

    private SolidWorksDimensionUpdateRequest CreateTrussMemberUpdateRequest(
        EditableTrussMemberItem member,
        string partFilePath,
        string dimensionName,
        string dimensionLabel,
        decimal value,
        string unit)
    {
        return new SolidWorksDimensionUpdateRequest
        {
            ParameterName = $"{member.Id}.{dimensionLabel}",
            DisplayName = $"{member.Name}{dimensionLabel}",
            PartFilePath = partFilePath,
            Configuration = string.Empty,
            DimensionName = dimensionName,
            Value = value.ToString(CultureInfo.InvariantCulture),
            Unit = string.IsNullOrWhiteSpace(unit) ? member.Unit : unit
        };
    }

    private async Task<string> ResolveLinkedRuleDimensionNameAsync(
        string partFilePath,
        IReadOnlyList<string> dimensionNameCandidates,
        Action<string> logWriter,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in dimensionNameCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var probeResult = await _solidWorksService.ProbeDimensionAsync(
                partFilePath,
                candidate,
                logWriter,
                cancellationToken);
            if (probeResult.Found)
            {
                return string.IsNullOrWhiteSpace(probeResult.ResolvedDimensionName)
                    ? candidate
                    : probeResult.ResolvedDimensionName;
            }
        }

        return string.Empty;
    }

    private IReadOnlyList<string> BuildLowerSectionLinkedCompensationExtrudeDimensionCandidates(
        string partFilePath,
        TrussLinkedParameterRule rule,
        Action<string> logWriter)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
            {
                candidates.Add(candidate);
                logWriter($"[LinkedPartCompensation] ExtrudeDimensionCandidate={candidate}");
            }
        }

        var scanItems = _dimensionScanCatalogService.LoadLatestResultOrEmpty().Items;
        var normalizedPartFilePath = NormalizeScanPartFilePath(partFilePath);
        var scanItemsWithPath = scanItems
            .Where(item => !string.IsNullOrWhiteSpace(item.PartFilePath))
            .ToList();
        var exactPathMatches = scanItemsWithPath
            .Where(item => string.Equals(
                NormalizeScanPartFilePath(item.PartFilePath),
                normalizedPartFilePath,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        IEnumerable<SolidWorksDimensionScanItem> selectedScanItems;
        if (exactPathMatches.Count > 0)
        {
            logWriter($"[LinkedPartCompensation] ScanMatchMode=FullPath; ScanMatches={exactPathMatches.Count}");
            selectedScanItems = exactPathMatches;
        }
        else
        {
            if (scanItemsWithPath.Count == 0)
            {
                logWriter("[LinkedPartCompensation] ScanMatchMode=FileNameFallback; Reason=ScanItemsMissingPartFilePath");
            }
            else
            {
                logWriter("[LinkedPartCompensation] ScanMatchMode=FileNameFallback; Reason=FullPathMatchNotFound");
            }

            selectedScanItems = scanItems.Where(item => string.Equals(
                Path.GetFileName(item.PartFilePath),
                Path.GetFileName(partFilePath),
                StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in selectedScanItems)
        {
            var fullName = string.IsNullOrWhiteSpace(item.FullDimensionName) ? item.DimensionName : item.FullDimensionName;
            if (string.IsNullOrWhiteSpace(fullName) ||
                !fullName.StartsWith("D1@", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ContainsLinkedCompensationExtrudeFeatureToken(fullName) ||
                ContainsLinkedCompensationExtrudeFeatureToken(item.FeatureName))
            {
                Add(fullName);
            }
        }

        foreach (var candidate in BuildPartSpecificDimensionNameCandidates(partFilePath, rule.DimensionNameCandidates))
        {
            Add(candidate);
        }

        return candidates;
    }

    private static string NormalizeScanPartFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(path.Trim());
    }

    private static IReadOnlyList<string> BuildPartSpecificDimensionNameCandidates(
        string partFilePath,
        IReadOnlyList<string> dimensionNameCandidates)
    {
        var orderedCandidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partName = Path.GetFileNameWithoutExtension(partFilePath);
        var partToken = string.IsNullOrWhiteSpace(partName)
            ? string.Empty
            : $"{partName}.Part";

        if (!string.IsNullOrWhiteSpace(partToken))
        {
            foreach (var candidate in dimensionNameCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) ||
                    !candidate.Contains(partToken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(candidate))
                {
                    orderedCandidates.Add(candidate);
                }
            }
        }

        foreach (var candidate in dimensionNameCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) ||
                candidate.Contains(".Part", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(candidate))
            {
                orderedCandidates.Add(candidate);
            }
        }

        foreach (var candidate in dimensionNameCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (seen.Add(candidate))
            {
                orderedCandidates.Add(candidate);
            }
        }

        return orderedCandidates;
    }

    private List<string> ResolveLinkedRulePartPaths(TrussLinkedParameterRule rule, Action<string> logWriter)
    {
        var matchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var targetPartFileName in rule.TargetPartFileNames)
        {
            if (string.IsNullOrWhiteSpace(targetPartFileName))
            {
                continue;
            }

            var fileName = Path.GetFileName(targetPartFileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            logWriter($"[LinkedRule] TargetPartFileName={fileName}");

            foreach (var matchedPath in Directory.EnumerateFiles(
                         _workspaceManager.WorkingModelFolder,
                         fileName,
                         SearchOption.AllDirectories))
            {
                if (IsUnderWorkingModelFolder(matchedPath))
                {
                    matchedPaths.Add(Path.GetFullPath(matchedPath));
                }
            }
        }

        foreach (var matchedPath in matchedPaths)
        {
            logWriter($"[LinkedRule] TargetPartCandidate={matchedPath}");
        }

        return matchedPaths.ToList();
    }

    private static bool TryGetUpperChordSectionWidthFromParameters(
        IReadOnlyList<ParameterItem> parameters,
        out decimal newUpperChordSectionMm)
    {
        newUpperChordSectionMm = 0m;
        var widthParameter = parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, TrussLinkedParameterRules.UpperChordSectionWidthParameterName, StringComparison.OrdinalIgnoreCase));
        var heightParameter = parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, TrussLinkedParameterRules.UpperChordSectionHeightParameterName, StringComparison.OrdinalIgnoreCase));

        if (widthParameter is null || heightParameter is null)
        {
            return false;
        }

        return decimal.TryParse(widthParameter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out newUpperChordSectionMm) ||
               decimal.TryParse(widthParameter.Value, NumberStyles.Float, CultureInfo.CurrentCulture, out newUpperChordSectionMm);
    }

    private static bool IsUpperChordMember(EditableTrussMemberItem member)
    {
        return string.Equals(member.Id, TrussLinkedParameterRules.UpperChordMemberId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(member.MemberRole, "UpperChord", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLowerSectionLinkedCompensationMember(EditableTrussMemberItem member)
    {
        return string.Equals(member.Id, LowerTrussMemberId, StringComparison.OrdinalIgnoreCase) ||
               member.Id.StartsWith("lower_chord", StringComparison.OrdinalIgnoreCase);
    }

    private static string SelectPreferredLowerSectionLinkedCompensationPartPath(IReadOnlyList<string> candidateParts)
    {
        if (candidateParts.Count == 0)
        {
            return string.Empty;
        }

        var preferredNonBackup = candidateParts.FirstOrDefault(path =>
            path.Contains($"{Path.DirectorySeparatorChar}桁架2{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains($"{Path.DirectorySeparatorChar}backup{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(preferredNonBackup))
        {
            return preferredNonBackup;
        }

        var nonBackup = candidateParts.FirstOrDefault(path =>
            !path.Contains($"{Path.DirectorySeparatorChar}backup{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(nonBackup)
            ? candidateParts[0]
            : nonBackup;
    }

    private static bool ContainsLinkedCompensationExtrudeFeatureToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("拉伸", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("拉伸-薄壁", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("凸台-拉伸", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Boss-Extrude", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Extrude", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveWorkingPartPath(SolidWorksTarget target, Action<string> logWriter)
    {
        foreach (var candidate in BuildPartPathCandidates(target))
        {
            if (!IsUnderWorkingModelFolder(candidate))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                logWriter($"已解析 SolidWorks 零件路径：{candidate}");
                return candidate;
            }
        }

        var fallback = BuildPartPathCandidates(target).FirstOrDefault(IsUnderWorkingModelFolder);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            logWriter($"未找到已存在的工作区零件路径，将按工作区映射路径继续尝试：{fallback}");
            return fallback;
        }

        return string.Empty;
    }

    private string ResolveConfiguredPartFilePath(string configuredPath, Action<string> logWriter)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        var resolvedPath = Path.IsPathRooted(configuredPath)
            ? _workspaceManager.ToWorkingPath(configuredPath)
            : Path.GetFullPath(Path.Combine(_workspaceManager.WorkingModelFolder, configuredPath));

        logWriter($"宸茶В鏋愭瀯浠堕厤缃浂浠惰矾寰勶細{resolvedPath}");
        return resolvedPath;
    }

    private string ResolveConfiguredPartFilePathEx(string relativePath, string configuredPath, Action<string> logWriter)
    {
        var effectivePath = !string.IsNullOrWhiteSpace(relativePath)
            ? relativePath
            : configuredPath;

        if (string.IsNullOrWhiteSpace(effectivePath))
        {
            return string.Empty;
        }

        var normalizedPath = effectivePath
            .Trim()
            .Replace('/', Path.DirectorySeparatorChar);

        string resolvedPath;
        if (Path.IsPathRooted(normalizedPath))
        {
            resolvedPath = _workspaceManager.ToWorkingPath(normalizedPath);
        }
        else
        {
            var workingFolderName = Path.GetFileName(
                _workspaceManager.WorkingModelFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var relativeCandidate = normalizedPath;
            if (!string.IsNullOrWhiteSpace(workingFolderName) &&
                relativeCandidate.StartsWith(workingFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                relativeCandidate = relativeCandidate[(workingFolderName.Length + 1)..];
            }

            resolvedPath = Path.GetFullPath(Path.Combine(_workspaceManager.WorkingModelFolder, relativeCandidate));
        }

        logWriter($"[TrussMapping] 解析构件配置零件路径：{resolvedPath}");
        return resolvedPath;
    }

    private static bool HasLegacyTrussMemberMapping(EditableTrussMemberItem member)
    {
        var hasPath = !string.IsNullOrWhiteSpace(member.RelativePartPath) ||
                      !string.IsNullOrWhiteSpace(member.PartFilePath);
        var hasDimensions = !string.IsNullOrWhiteSpace(member.WidthDimensionName) ||
                            !string.IsNullOrWhiteSpace(member.HeightDimensionName) ||
                            !string.IsNullOrWhiteSpace(member.ThicknessDimensionName);
        return hasPath && hasDimensions;
    }

    private static bool IsUsablePartMapping(EditableTrussMemberPartMapping mapping, TrussMemberCommandParseResult parseResult)
    {
        var hasPath = !string.IsNullOrWhiteSpace(mapping.RelativePartPath) ||
                      !string.IsNullOrWhiteSpace(mapping.PartFilePath);
        if (!hasPath)
        {
            return false;
        }

        if (parseResult.Width.HasValue && string.IsNullOrWhiteSpace(mapping.WidthDimensionName))
        {
            return false;
        }

        if (parseResult.Height.HasValue && string.IsNullOrWhiteSpace(mapping.HeightDimensionName))
        {
            return false;
        }

        if (parseResult.Thickness.HasValue &&
            string.IsNullOrWhiteSpace(mapping.ThicknessDimensionName) &&
            (string.IsNullOrWhiteSpace(mapping.InnerWidthDimensionName) || string.IsNullOrWhiteSpace(mapping.InnerHeightDimensionName)))
        {
            return false;
        }

        return true;
    }

    private static string BuildSkippedPartMappingReason(EditableTrussMemberPartMapping mapping, TrussMemberCommandParseResult parseResult)
    {
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(mapping.RelativePartPath) &&
            string.IsNullOrWhiteSpace(mapping.PartFilePath))
        {
            reasons.Add("缂哄皯 RelativePartPath/PartFilePath");
        }

        if (parseResult.Width.HasValue && string.IsNullOrWhiteSpace(mapping.WidthDimensionName))
        {
            reasons.Add("缂哄皯 WidthDimensionName");
        }

        if (parseResult.Height.HasValue && string.IsNullOrWhiteSpace(mapping.HeightDimensionName))
        {
            reasons.Add("缂哄皯 HeightDimensionName");
        }

        if (parseResult.Thickness.HasValue && string.IsNullOrWhiteSpace(mapping.ThicknessDimensionName))
        {
            reasons.Add("缂哄皯 ThicknessDimensionName");
        }

        return reasons.Count == 0 ? "未命中当前修改所需字段" : string.Join("；", reasons);
    }

    private string ResolveConfiguredPartFilePathPortable(string relativePath, string configuredPath, Action<string> logWriter)
    {
        var effectivePath = !string.IsNullOrWhiteSpace(relativePath)
            ? relativePath
            : configuredPath;

        if (string.IsNullOrWhiteSpace(effectivePath))
        {
            return string.Empty;
        }

        var normalizedPath = NormalizePortableConfiguredPath(effectivePath);
        var workingModelPath = _workspaceManager.WorkingModelFolder;

        logWriter($"[TrussMapping] RawRelativePartPath={relativePath}");
        logWriter($"[TrussMapping] RawConfiguredPath={configuredPath}");
        logWriter($"[TrussMapping] WorkingModelPath={workingModelPath}");

        var candidates = BuildPortableConfiguredPartPathCandidates(normalizedPath, workingModelPath);
        string? firstExistingPath = null;

        foreach (var candidate in candidates)
        {
            var exists = File.Exists(candidate.Path);
            logWriter($"[TrussMapping] CandidatePath[{candidate.Label}]={candidate.Path}; Exists={exists}");
            if (exists && firstExistingPath is null)
            {
                firstExistingPath = candidate.Path;
            }
        }

        var finalPath = firstExistingPath ?? SelectBestPortableFallbackCandidate(candidates);
        var finalExists = !string.IsNullOrWhiteSpace(finalPath) && File.Exists(finalPath);
        logWriter($"[TrussMapping] FinalPartPath={finalPath}; Exists={finalExists}");
        return finalPath;
    }

    private static string NormalizePortableConfiguredPath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('/', Path.DirectorySeparatorChar);
    }

    private List<(string Label, string Path)> BuildPortableConfiguredPartPathCandidates(string normalizedPath, string workingModelPath)
    {
        var candidates = new List<(string Label, string Path)>();

        if (Path.IsPathRooted(normalizedPath))
        {
            AddPortableCandidatePath(candidates, "AbsoluteRaw", normalizedPath);
            var rootedAfterNewModelPath = TryTrimPortableAfterDirectorySegment(normalizedPath, "新模型");
            if (!string.IsNullOrWhiteSpace(rootedAfterNewModelPath))
            {
                AddPortableCandidatePath(candidates, "WorkingPlusAfterNewModel", Path.Combine(workingModelPath, rootedAfterNewModelPath));
            }

            var rootedFromTrussPath = TryTrimPortableFromTrussFolder(normalizedPath);
            if (!string.IsNullOrWhiteSpace(rootedFromTrussPath))
            {
                AddPortableCandidatePath(candidates, "WorkingPlusFromTrussFolder", Path.Combine(workingModelPath, rootedFromTrussPath));
            }

            return candidates;
        }

        AddPortableCandidatePath(candidates, "WorkingPlusRawRelative", Path.Combine(workingModelPath, normalizedPath));

        var afterNewModelPath = TryTrimPortableAfterDirectorySegment(normalizedPath, "新模型");
        if (!string.IsNullOrWhiteSpace(afterNewModelPath))
        {
            AddPortableCandidatePath(candidates, "WorkingPlusAfterNewModel", Path.Combine(workingModelPath, afterNewModelPath));
        }

        var fromTrussPath = TryTrimPortableFromTrussFolder(normalizedPath);
        if (!string.IsNullOrWhiteSpace(fromTrussPath))
        {
            AddPortableCandidatePath(candidates, "WorkingPlusFromTrussFolder", Path.Combine(workingModelPath, fromTrussPath));
        }

        return candidates;
    }

    private static void AddPortableCandidatePath(ICollection<(string Label, string Path)> candidates, string label, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalizedCandidatePath = Path.GetFullPath(path);
        if (candidates.Any(item => string.Equals(item.Path, normalizedCandidatePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add((label, normalizedCandidatePath));
    }

    private static string TryTrimPortableFromTrussFolder(string normalizedPath)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            normalizedPath,
            @"妗佹灦\d+.*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success
            ? match.Value.Replace('/', Path.DirectorySeparatorChar)
            : string.Empty;
    }

    private static string TryTrimPortableAfterDirectorySegment(string normalizedPath, string segmentName)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(segmentName))
        {
            return string.Empty;
        }

        var segments = normalizedPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < segments.Length; index++)
        {
            if (!string.Equals(segments[index], segmentName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index >= segments.Length - 1)
            {
                return string.Empty;
            }

            return Path.Combine(segments[(index + 1)..]);
        }

        return string.Empty;
    }

    private static string SelectBestPortableFallbackCandidate(IReadOnlyList<(string Label, string Path)> candidates)
    {
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        var preferredFallback = candidates.FirstOrDefault(item =>
            string.Equals(item.Label, "WorkingPlusAfterNewModel", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Label, "WorkingPlusFromTrussFolder", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(preferredFallback.Path))
        {
            return preferredFallback.Path;
        }

        return candidates[0].Path;
    }

    private IEnumerable<string> BuildPartPathCandidates(SolidWorksTarget target)
    {
        foreach (var candidate in ExpandCandidatePaths(target.OutputFile, preferWorkingFolder: true))
        {
            yield return candidate;
        }

        foreach (var candidate in ExpandCandidatePaths(target.SourceFile, preferWorkingFolder: false))
        {
            yield return candidate;
        }
    }

    private IEnumerable<string> ExpandCandidatePaths(string path, bool preferWorkingFolder)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        if (Path.IsPathRooted(path))
        {
            yield return _workspaceManager.ToWorkingPath(path);
            yield break;
        }

        if (preferWorkingFolder)
        {
            yield return Path.GetFullPath(Path.Combine(_workspaceManager.WorkingModelFolder, path));
            yield return _workspaceManager.ToWorkingPath(Path.Combine(_workspaceManager.InitialModelFolder, path));
            yield break;
        }

        var initialCandidate = Path.GetFullPath(Path.Combine(_workspaceManager.InitialModelFolder, path));
        yield return _workspaceManager.ToWorkingPath(initialCandidate);
        yield return Path.GetFullPath(Path.Combine(_workspaceManager.WorkingModelFolder, path));
    }

    private bool IsUnderWorkingModelFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path.Trim());
        var normalizedWorkingFolder = Path.GetFullPath(_workspaceManager.WorkingModelFolder.Trim());
        if (!normalizedWorkingFolder.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedWorkingFolder += Path.DirectorySeparatorChar;
        }

        return normalizedPath.StartsWith(normalizedWorkingFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> GetOrderedActions(IEnumerable<string> actions)
    {
        var actionSet = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);
        var orderedActions = new List<string>();

        if (actionSet.Contains(ResetModelWorkspaceAction))
        {
            orderedActions.Add(ResetModelWorkspaceAction);
        }

        if (actionSet.Contains(QueryEditableParametersAction))
        {
            orderedActions.Add(QueryEditableParametersAction);
        }

        if (actionSet.Contains(ScanSolidWorksDimensionsAction))
        {
            orderedActions.Add(ScanSolidWorksDimensionsAction);
        }

        if (actionSet.Contains(RecommendTrussParameterMappingAction))
        {
            orderedActions.Add(RecommendTrussParameterMappingAction);
        }

        if (actionSet.Contains(ConfirmTrussParameterMappingAction))
        {
            orderedActions.Add(ConfirmTrussParameterMappingAction);
        }

        if (actionSet.Contains(OpenWorkingModelAction))
        {
            orderedActions.Add(OpenWorkingModelAction);
        }

        if (actionSet.Contains(UpdateSolidWorksDimensionsAction))
        {
            orderedActions.Add(UpdateSolidWorksDimensionsAction);
        }

        return orderedActions;
    }

    private static bool IsExecutableSolidWorksParameter(ParameterItem parameter)
    {
        return string.Equals(parameter.MappingStatus, "OK", StringComparison.OrdinalIgnoreCase) &&
               parameter.SolidWorksTargets.Count > 0;
    }

    private static bool LooksLikeTrussMemberUpdateIntent(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalized = input.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var mentionsTrussMember = normalized.Contains("寮︽潌", StringComparison.Ordinal) ||
                                  normalized.Contains("涓婂鸡", StringComparison.Ordinal) ||
                                  normalized.Contains("涓嬪鸡", StringComparison.Ordinal);
        var mentionsSectionOrDimension = normalized.Contains("鎴潰", StringComparison.Ordinal) ||
                                         normalized.Contains("澹佸帤", StringComparison.Ordinal) ||
                                         normalized.Contains("瀹藉害", StringComparison.Ordinal) ||
                                         normalized.Contains("楂樺害", StringComparison.Ordinal) ||
                                         normalized.Contains("脳", StringComparison.Ordinal) ||
                                         normalized.Contains("x", StringComparison.Ordinal) ||
                                         normalized.Contains("*", StringComparison.Ordinal);
        return mentionsTrussMember && mentionsSectionOrDimension;
    }

    private string BuildScanGuidanceReply(SolidWorksDimensionScanResult scanResult)
    {
        return _editableTrussMemberCatalogService.BuildQueryableEditableParametersReply();
    }

    private static EditableTrussMemberItem CloneEditableTrussMemberItem(EditableTrussMemberItem item)
    {
        return new EditableTrussMemberItem
        {
            Id = item.Id,
            Name = item.Name,
            Aliases = [.. item.Aliases],
            Description = item.Description,
            MemberRole = item.MemberRole,
            SectionType = item.SectionType,
            PartFilePath = item.PartFilePath,
            ComponentName = item.ComponentName,
            WidthDimensionName = item.WidthDimensionName,
            HeightDimensionName = item.HeightDimensionName,
            ThicknessDimensionName = item.ThicknessDimensionName,
            Unit = item.Unit,
            MinWidth = item.MinWidth,
            MaxWidth = item.MaxWidth,
            MinHeight = item.MinHeight,
            MaxHeight = item.MaxHeight,
            MinThickness = item.MinThickness,
            MaxThickness = item.MaxThickness,
            Example = item.Example,
            Enabled = item.Enabled
        };
    }

    private static void BackupEditableTrussMemberConfig(string configPath, Action<string> logWriter)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            throw new FileNotFoundException("未找到 editable-truss-members.json，无法执行备份。", configPath);
        }

        var directory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        var backupFileName = $"editable-truss-members.backup-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        var backupPath = Path.Combine(directory, backupFileName);
        File.Copy(configPath, backupPath, overwrite: false);
        logWriter($"宸插浠藉弬鏁版槧灏勯厤缃細{backupPath}");
    }
}

public sealed record CommandDispatchResult(
    bool Handled,
    bool SuppressDefaultReply,
    bool Succeeded,
    IReadOnlyList<string> AssistantMessages,
    SolidWorksDimensionScanResult? ScanResult)
{
    public static CommandDispatchResult NotHandled { get; } = new(false, false, false, Array.Empty<string>(), null);
}

internal sealed record DimensionUpdateDispatchResult(bool Handled, bool RequiresFollowUp, bool Succeeded)
{
    public static DimensionUpdateDispatchResult FollowUpRequired { get; } = new(false, true, false);
}

internal sealed record LowerTrussDimensionReportItem(string DimensionName, decimal? BeforeValue, decimal? AfterValue);

internal sealed record LowerTrussReportContext(
    string MemberName,
    string ModelPath,
    decimal OuterSize,
    decimal Thickness,
    decimal InnerSize,
    IReadOnlyList<LowerTrussDimensionReportItem> Dimensions,
    string BackupPath);

internal sealed record LowerTrussBackupResult(bool Succeeded, string Message, string BackupPath);

internal sealed record LinkedLowerChordPartResult(bool Enabled, bool PartFound, string PartPath)
{
    public static LinkedLowerChordPartResult Disabled { get; } = new(false, false, string.Empty);
}

internal sealed record LinkedLowerChordPartPlan(
    bool Enabled,
    bool PartFound,
    string PartPath,
    IReadOnlyList<SolidWorksDimensionUpdateRequest> Requests)
{
    public static LinkedLowerChordPartPlan Disabled { get; } =
        new(false, false, string.Empty, Array.Empty<SolidWorksDimensionUpdateRequest>());
}

internal sealed record LinkedLowerChordPartExecutionResult(
    bool Enabled,
    bool PartFound,
    bool UpdateSucceeded,
    string PartPath,
    int RequestsCreatedCount)
{
    public static LinkedLowerChordPartExecutionResult Disabled { get; } = new(false, false, false, string.Empty, 0);
}

internal sealed record LowerSectionLinkedCompensationPlan(
    bool Enabled,
    bool PartFound,
    bool Applied,
    string PartPath,
    string ResultMessage,
    IReadOnlyList<SolidWorksDimensionUpdateRequest> Requests)
{
    public static LowerSectionLinkedCompensationPlan Disabled { get; } =
        new(false, false, false, string.Empty, string.Empty, Array.Empty<SolidWorksDimensionUpdateRequest>());
}

internal sealed record LinkedLowerTrussSectionPartPlan(
    bool Enabled,
    bool PartFound,
    string PartPath,
    IReadOnlyList<SolidWorksDimensionUpdateRequest> Requests)
{
    public static LinkedLowerTrussSectionPartPlan Disabled { get; } =
        new(false, false, string.Empty, Array.Empty<SolidWorksDimensionUpdateRequest>());
}

