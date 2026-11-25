using System.Globalization;
using System.IO;
using System.Text;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Chat;
using LlmWpfPrototype.Services.Simulation;

namespace LlmWpfPrototype.ViewModels;

public sealed partial class MainViewModel
{
    private AssistantRuntimeContextSnapshot BuildRuntimeContextSnapshot()
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
        var isLocalKnowledgeAvailable = Directory.Exists(
            Path.Combine(AppContext.BaseDirectory, "KnowledgeBase", "GB_T_3811_2008", "verified"));

        var pendingCadSummary = _pendingBusinessPlan is null
            ? null
            : BuildPendingBusinessPlanSummary();
        var pendingSlotSummary = _pendingModificationContext?.IsActive == true
            ? BuildPendingSlotSummary(_pendingModificationContext)
            : null;

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
            RecentActions: _memoryStore.GetRecentActions());
    }

    private string BuildWelcomeMessage()
    {
        var snapshot = BuildRuntimeContextSnapshot();
        var apdlCapability = snapshot.IsApdlLauncherAvailable
            ? "启动本地 ANSYS APDL 启动器、读取轻量化基线"
            : "咨询 APDL / ANSYS 分析准备与脚本起草";
        var kbCapability = snapshot.IsLocalKnowledgeBaseAvailable
            ? "，也可基于本地 GB/T 3811-2008 资料解答工程规范问题"
            : string.Empty;

        return $"您好，我是桥梁检查车智能设计助手。我可以协助你操作 SolidWorks 工作模型、查询或修改已配置的桁架构件尺寸（修改前会先请你确认）、{apdlCapability}{kbCapability}。请直接说明需求，或输入“你能做什么”。";
    }

    private string BuildDynamicGreetingReply()
    {
        var snapshot = BuildRuntimeContextSnapshot();
        var sb = new StringBuilder();
        sb.Append(snapshot.IsSolidWorksModelOpened
            ? $"你好！当前 SolidWorks 模型已打开（{(string.IsNullOrWhiteSpace(snapshot.ActiveDocumentName) ? "当前活动模型" : snapshot.ActiveDocumentName)}）。"
            : "你好！当前尚未检测到已打开的 SolidWorks 工作模型。");

        sb.Append("我可以帮你：");
        sb.Append(snapshot.IsSolidWorksModelOpened
            ? "查询可编辑杆件参数、修改已配置的上/下弦杆或下桁架截面与壁厚（修改前会先让你确认）"
            : "打开当前 SolidWorks 工作模型、查询可编辑参数、修改已配置的桁架构件尺寸");

        if (snapshot.IsApdlLauncherAvailable)
        {
            sb.Append("，也可以直接启动本地 ANSYS APDL 启动器或读取轻量化基线");
        }
        else
        {
            sb.Append("，也可以协助准备 APDL / ANSYS 分析流程或起草脚本");
        }

        if (snapshot.IsLocalKnowledgeBaseAvailable)
        {
            sb.Append("，并支持工程规范资料问答");
        }

        sb.Append('。');

        if (!string.IsNullOrWhiteSpace(snapshot.PendingCadEditSummary))
        {
            sb.Append($"（提示：你还有一项待确认修改：{snapshot.PendingCadEditSummary}，可发送“确认修改”或“取消修改”。）");
        }
        else if (!snapshot.IsSolidWorksModelOpened)
        {
            sb.Append("你可以先说“打开当前模型”“查询可编辑参数”，或直接说“打开 APDL”。");
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

        // Allow polite editable-parameter queries, polite model-open requests, live parameter queries,
        // and APDL launcher candidates to stay action-capable instead of blanket-blocking every '?' / '吗'.
        if (isQuestionOnlyInput &&
            !apdlLaunchCandidate &&
            !hasExplicitModificationCommand &&
            !IsLiveParameterQuery(input) &&
            !ChatTurnRouter.IsPoliteEditableParameterQuery(input, normalizedInput) &&
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
