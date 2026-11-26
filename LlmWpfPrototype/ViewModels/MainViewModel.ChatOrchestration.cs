using System.Globalization;
using System.IO;
using System.Text;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Chat;
using LlmWpfPrototype.Services.Design;
using LlmWpfPrototype.Services.Rag;
using LlmWpfPrototype.Services.Simulation;

namespace LlmWpfPrototype.ViewModels;

public sealed partial class MainViewModel
{
    private AssistantRuntimeContextSnapshot BuildRuntimeContextSnapshot(string? currentUserInput = null)
    {
        var isModelOpened = _solidWorksService.TryGetActiveDocumentInfoWithoutStartingSolidWorks(out var activeDocumentPath);
        var activeDocumentName = string.IsNullOrWhiteSpace(activeDocumentPath)
            ? string.Empty
            : Path.GetFileName(activeDocumentPath);

        var editableMembers = BuildEditableParameterLines();
        var isApdlLauncherConfigured = !string.IsNullOrWhiteSpace(_simulationOptions.LauncherPath);
        var isApdlLauncherAvailable = IsConfiguredAnsysLauncherAvailable();
        var isApdlSolverConfigured = !string.IsNullOrWhiteSpace(_simulationOptions.SolverPath);
        var isApdlSolverAvailable = IsConfiguredAnsysSolverAvailable();
        var isLightweightStudyConfigured = IsConfiguredLightweightStudyAvailable();
        var kbSearchService = LocalKnowledgeSearchService.CreateForApplication();
        var kbCatalogSummary = kbSearchService.BuildKnowledgeBaseCatalogSummary();
        var isLocalKnowledgeAvailable = !kbCatalogSummary.StartsWith("未检测到", StringComparison.Ordinal);

        var pendingCadSummary = _pendingBusinessPlan is null
            ? null
            : BuildPendingBusinessPlanSummary();
        var pendingSlotSummary = _pendingModificationContext?.IsActive == true
            ? BuildPendingSlotSummary(_pendingModificationContext)
            : null;

        var activeProj = _projectStore?.ActiveProject;
        var projectContextBlock = BuildProjectLongTermMemoryPromptBlock();
        var liveDimensionsBlock = BuildLiveStructureDimensionsPromptBlock();
        var engineeringKnowledgeBlock = BridgeCarDesignKnowledgeService.BuildContextualEngineeringGuidance(
            currentUserInput,
            activeProj);

        return new AssistantRuntimeContextSnapshot(
            IsSolidWorksModelOpened: isModelOpened,
            ActiveDocumentName: activeDocumentName,
            IsWorkspaceInitialized: _workspaceManager.IsWorkspaceInitialized,
            EditableMemberSummaries: editableMembers,
            IsApdlLauncherConfigured: isApdlLauncherConfigured,
            IsApdlLauncherAvailable: isApdlLauncherAvailable,
            IsApdlSolverConfigured: isApdlSolverConfigured,
            IsApdlSolverAvailable: isApdlSolverAvailable,
            IsLightweightStudyConfigured: isLightweightStudyConfigured,
            IsLocalKnowledgeBaseAvailable: isLocalKnowledgeAvailable,
            PendingCadEditSummary: pendingCadSummary,
            PendingResetConfirmation: _pendingResetWorkspaceConfirmation,
            PendingSlotSummary: pendingSlotSummary,
            LastModifiedParameterSummary: string.IsNullOrWhiteSpace(_lastModifiedParameterSummary) ? null : _lastModifiedParameterSummary,
            ApdlLauncherStartCount: _memoryStore.ApdlLauncherStartCount,
            LastApdlLauncherStartedAt: _memoryStore.LastApdlLauncherStartedAt,
            RecentActions: _memoryStore.GetRecentActions(),
            ProjectContextBlock: projectContextBlock,
            LiveStructureDimensionsBlock: liveDimensionsBlock,
            EngineeringKnowledgeGuidanceBlock: engineeringKnowledgeBlock,
            LocalKnowledgeBaseCatalogSummary: isLocalKnowledgeAvailable ? kbCatalogSummary : null);
    }

    private string BuildWelcomeMessage()
    {
        var profile = DesignBoundaryProfileLoader.Load();
        var v = profile.Vehicle;

        if (_projectStore is not null && ActiveProject is { } active)
        {
            if (active.CurrentVersionNumber > 0)
            {
                return $"已进入【{active.Name}（{active.VersionTag}）】· 当前上弦杆 `{active.UpperChord.SpecText} mm`、下弦杆 `{active.LowerChord.SpecText} mm`（已绑定 {v.StandardModelCode} 实体模型及 GB/T 3811 / JT/T 1430 / GB 50017 三规范边界）。";
            }

            return $"已进入【{active.Name}（{active.VersionTag}）】· 已绑定实车 {v.StandardModelCode}（{v.MaterialGrade}，上/下弦杆 `{active.UpperChord.SpecText} mm`）与 GB/T 3811 / JT/T 1430 / GB 50017 三规范边界，可直接讨论改型、预检改模或联动 APDL 仿真。";
        }

        return $"已绑定实车 {v.StandardModelCode}（{v.MaterialGrade}，母型 `{v.BaselineWidthMm:0.#}×{v.BaselineHeightMm:0.#}×{v.BaselineThicknessMm:0.#} mm`）与 GB/T 3811 / JT/T 1430 / GB 50017 三规范边界，可直接开展设计讨论、参数化改模与仿真校核。";
    }

    private string BuildDynamicGreetingReply()
    {
        var snapshot = BuildRuntimeContextSnapshot();
        var active = ActiveProject;
        var upper = active?.UpperChord ?? MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆");
        var lower = active?.LowerChord ?? MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆");
        var projectLabel = active is not null ? $"【{active.Name}（{active.VersionTag}）】" : "当前设计项目";

        var sb = new StringBuilder();
        sb.Append($"你好！当前{projectLabel}上弦杆为 `{upper.SpecText} mm`、下弦杆为 `{lower.SpecText} mm`");
        sb.Append(snapshot.IsSolidWorksModelOpened
            ? $"，SolidWorks 工作模型已打开（{(string.IsNullOrWhiteSpace(snapshot.ActiveDocumentName) ? "当前活动模型" : snapshot.ActiveDocumentName)}）"
            : "，三本规范边界与工作区均已就绪");
        sb.Append('。');

        if (!string.IsNullOrWhiteSpace(snapshot.PendingCadEditSummary))
        {
            sb.Append($"目前还有一项待确认改模：{snapshot.PendingCadEditSummary}，可直接发送“确认修改”或“取消修改”。");
            return sb.ToString();
        }

        if (active is { CurrentVersionNumber: > 0 })
        {
            sb.Append(snapshot.IsApdlLauncherAvailable
                ? "这轮是继续迭代另一组弦杆、复核宽厚比与 7.35 m 悬臂挠度红线，还是直接启动 APDL 做仿真对比？"
                : "这轮是继续迭代另一组弦杆，还是先对照三本规范复核宽厚比与 7.35 m 悬臂挠度红线？");
        }
        else
        {
            sb.Append(snapshot.IsSolidWorksModelOpened
                ? "这轮想先评估减薄/扩廓轻量化方案、直接改一组三维截面看规范预检，还是打开 APDL 准备仿真工况？"
                : "这轮想先评估下弦杆减薄或扩廓减重方案、直接预检调整截面，还是先打开 SolidWorks 工作模型看一下？");
        }

        return sb.ToString();
    }

    private static string BuildPendingSlotSummary(PendingModificationContext context)
    {
        if (string.Equals(context.MissingSlot, "size", StringComparison.OrdinalIgnoreCase))
        {
            return $"已指定构件“{context.TargetMemberDisplayName}”，等待补充目标截面尺寸或壁厚";
        }

        if (string.Equals(context.MissingSlot, "member", StringComparison.OrdinalIgnoreCase))
        {
            var valueText = context.SectionWidth.HasValue && context.SectionHeight.HasValue
                ? FormatSectionSpec(context.SectionWidth.Value, context.SectionHeight.Value, context.WallThickness)
                : context.WallThickness.HasValue
                    ? $"壁厚 {context.WallThickness.Value.ToString(CultureInfo.InvariantCulture)} mm"
                    : "目标尺寸";
            return $"已指定数值“{valueText}”，等待确认应用于上弦杆、下弦杆还是下桁架";
        }

        return "有一项未补全的参数修改意图";
    }

    private static bool IsNegativeActionIntent(string text)
    {
        if (IsNegativeOrCancelModificationText(text))
        {
            return true;
        }

        var normalized = NormalizeIntentText(text);
        return normalized.Contains("不要打开", StringComparison.Ordinal) ||
               normalized.Contains("别打开", StringComparison.Ordinal) ||
               normalized.Contains("不打开", StringComparison.Ordinal) ||
               normalized.Contains("不用打开", StringComparison.Ordinal) ||
               normalized.Contains("不需要打开", StringComparison.Ordinal) ||
               normalized.Contains("不要启动", StringComparison.Ordinal) ||
               normalized.Contains("别启动", StringComparison.Ordinal) ||
               normalized.Contains("不启动", StringComparison.Ordinal) ||
               normalized.Contains("不用启动", StringComparison.Ordinal) ||
               normalized.Contains("不需要启动", StringComparison.Ordinal) ||
               normalized.Contains("不要运行", StringComparison.Ordinal) ||
               normalized.Contains("别运行", StringComparison.Ordinal) ||
               normalized.Contains("不要重置", StringComparison.Ordinal) ||
               normalized.Contains("别重置", StringComparison.Ordinal) ||
               normalized.Contains("先别", StringComparison.Ordinal) ||
               normalized.Contains("暂不", StringComparison.Ordinal);
    }

    private ReplyOnlyIntentContext? BuildReplyOnlyIntentIfNeeded(
        string input,
        string normalizedInput,
        bool isNegativeOrCancel,
        bool isQuestionOnlyInput,
        bool apdlLaunchCandidate,
        bool hasExplicitModificationCommand,
        UserIntentKind classifiedIntent)
    {
        if (isNegativeOrCancel)
        {
            return new ReplyOnlyIntentContext(
                UserIntentKind.Unknown,
                "CancelOrNegation",
                "明白，本轮不会执行模型修改、打开或软件启动操作。");
        }

        if (IsModificationStatusQueryText(input))
        {
            return new ReplyOnlyIntentContext(
                UserIntentKind.Unknown,
                "ModificationStatusQuery",
                BuildModificationStatusReply());
        }

        if (IsModelStatusQuestion(input))
        {
            return new ReplyOnlyIntentContext(
                UserIntentKind.Unknown,
                "ModelStatusQuestion",
                BuildModelStatusReply());
        }

        if (IsResetCapabilityQuestion(input))
        {
            return new ReplyOnlyIntentContext(
                UserIntentKind.Unknown,
                "ResetCapabilityQuestion",
                BuildResetCapabilityReply());
        }

        if (TryBuildParameterSupplementConsultationReply(input, normalizedInput, out var consultationReply))
        {
            return new ReplyOnlyIntentContext(
                UserIntentKind.Unknown,
                "ParameterSupplementConsultation",
                consultationReply);
        }

        if (!apdlLaunchCandidate &&
            classifiedIntent is UserIntentKind.Greeting or UserIntentKind.CapabilityQuestion or UserIntentKind.NextStepQuery)
        {
            var fallback = classifiedIntent == UserIntentKind.Greeting
                ? BuildDynamicGreetingReply()
                : BuildLocalIntentReply(classifiedIntent, input);
            return new ReplyOnlyIntentContext(
                classifiedIntent,
                classifiedIntent.ToString(),
                fallback);
        }

        if (IsOpenModelCapabilityQuestion(input, normalizedInput))
        {
            return new ReplyOnlyIntentContext(
                UserIntentKind.CapabilityQuestion,
                "OpenModelCapabilityQuestion",
                BuildQuestionOnlySafetyReply(input));
        }

        if (isQuestionOnlyInput && ChatTurnRouter.IsCurrentStructureDimensionQuestion(input, normalizedInput))
        {
            return new ReplyOnlyIntentContext(
                UserIntentKind.Unknown,
                "CurrentStructureDimensionQuestion",
                $"当前杆件截面尺寸（外宽×外高×壁厚）：{CurrentUpperChordHud}；{CurrentLowerChordHud}。");
        }

        // Allow polite editable-parameter queries, explicit/polite model-open requests, live parameter queries,
        // and APDL launcher candidates to stay action-capable instead of blanket-blocking every '?' / '吗'.
        if (isQuestionOnlyInput &&
            !apdlLaunchCandidate &&
            !hasExplicitModificationCommand &&
            !IsLiveParameterQuery(input) &&
            !ChatTurnRouter.IsPoliteEditableParameterQuery(input, normalizedInput) &&
            !IsExplicitOpenModelCommand(input, normalizedInput) &&
            !IsPoliteOpenModelExecutionRequest(input, normalizedInput))
        {
            return new ReplyOnlyIntentContext(
                UserIntentKind.Unknown,
                "PureQuestion",
                BuildQuestionOnlySafetyReply(input));
        }

        return null;
    }

    private async Task<bool> TryHandleSimulationActionFromLlmAsync(LlmParseResult parseResult, string userInput)
    {
        var actions = (parseResult.Actions ?? [])
            .Concat(parseResult.Commands ?? [])
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Select(action => action.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (actions.Any(action => string.Equals(action, "read_lightweight_baseline", StringComparison.OrdinalIgnoreCase)))
        {
            if (IsNegativeActionIntent(userInput))
            {
                const string reply = "明白，本轮不会读取轻量化基线。";
                AddAssistantMessage(reply);
                AppendConversationHistory(userInput, reply);
                return true;
            }

            await HandleSimulationChatAsync(userInput, explicitCommand: SimulationCommand.ReadBaseline);
            return true;
        }

        if (actions.Any(action => string.Equals(action, "run_lightweight_study", StringComparison.OrdinalIgnoreCase)))
        {
            if (IsNegativeActionIntent(userInput) || IsQuestionOnlyInput(userInput))
            {
                const string reply = "当前未执行轻量化仿真。如需运行，请直接发送“运行轻量化仿真”。";
                AddAssistantMessage(reply);
                AppendConversationHistory(userInput, reply);
                return true;
            }

            await HandleSimulationChatAsync(userInput, explicitCommand: SimulationCommand.RunStudy);
            return true;
        }

        return false;
    }

    private static string BuildDispatchActionAuditName(LlmParseResult? parseResult)
    {
        var actions = (parseResult?.Actions ?? [])
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Select(action => action.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return actions.Length == 0 ? "dispatch_command" : string.Join("+", actions);
    }

    private string BuildDispatchActionAuditSummary(LlmParseResult? parseResult, CommandDispatchResult dispatchResult)
    {
        var actions = parseResult?.Actions ?? [];
        if (actions.Any(action => string.Equals(action, "update_solidworks_dimensions", StringComparison.OrdinalIgnoreCase)))
        {
            var modSummary = BuildModificationSummary(parseResult, dispatchResult);
            return dispatchResult.Succeeded ? $"修改尺寸：{modSummary}" : $"尝试修改尺寸失败（{modSummary}）";
        }

        if (actions.Any(action => string.Equals(action, "open_working_model", StringComparison.OrdinalIgnoreCase)))
        {
            return dispatchResult.Succeeded ? "打开 SolidWorks 工作模型" : "打开 SolidWorks 工作模型失败";
        }

        if (actions.Any(action => string.Equals(action, "reset_model_workspace", StringComparison.OrdinalIgnoreCase)))
        {
            return dispatchResult.Succeeded ? "重置模型工作区为初始模型" : "重置模型工作区失败";
        }

        if (actions.Any(action => string.Equals(action, "query_editable_parameters", StringComparison.OrdinalIgnoreCase)))
        {
            return "查询当前可编辑杆件参数";
        }

        return dispatchResult.Succeeded ? "执行业务命令完成" : "执行业务命令失败";
    }
}
