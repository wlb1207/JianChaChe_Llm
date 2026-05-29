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
            assistantMessages.Add($"SolidWorks 尺寸更新失败：{ex.Message}");
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

        var updateRequests = await BuildTrussMemberDimensionUpdateRequestsWithPartMappingsAsync(
            targetMembers,
            parseResult,
            logWriter,
            cancellationToken);

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

        var success = await _solidWorksService.UpdateDimensionsAsync(updateRequests, logWriter, cancellationToken);
        if (success)
        {
            return new DimensionUpdateDispatchResult(true, false, true);
        }

        assistantMessages.Add("桁架构件尺寸更新失败，请检查高级调试日志。");
        return new DimensionUpdateDispatchResult(true, false, false);
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

            var resolvedDimensionName = await ResolveLinkedRuleDimensionNameAsync(
                partFilePath,
                rule.DimensionNameCandidates,
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
                    ResolveConfiguredPartFilePathPortable(item.Mapping.RelativePartPath, item.Mapping.PartFilePath, logWriter),
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

