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
        var swDiag = _solidWorksService.GetSessionDiagnostics();
        var isModelOpened = swDiag.IsWorkingModelReady;
        var activeDocumentName = !string.IsNullOrWhiteSpace(swDiag.ActiveDocumentName)
            ? swDiag.ActiveDocumentName
            : (string.IsNullOrWhiteSpace(swDiag.OpenWorkingAssemblyPath)
                ? string.Empty
                : Path.GetFileName(swDiag.OpenWorkingAssemblyPath));
        var solidWorksStatusDetail = swDiag.State switch
        {
            SolidWorksSessionState.ConnectedWorkingAssembly =>
                $"已打开并连接当前项目工作总装配（文档：{activeDocumentName}）",
            SolidWorksSessionState.ConnectedDifferentDocument when swDiag.HasOpenWorkingAssemblyInSession =>
                $"已连接 SolidWorks（当前激活：{swDiag.ActiveDocumentName}，本项目工作总装配已在后台打开，改模时会自动切回）",
            SolidWorksSessionState.ConnectedDifferentDocument =>
                $"已连接 SolidWorks，但当前打开的是“{swDiag.ActiveDocumentName}”，不属于当前项目工作副本目录（需发送“打开当前模型”切换至本项目工作副本）",
            SolidWorksSessionState.ProcessRunningComUnavailable =>
                $"检测到本机有 {swDiag.RunningProcessCount} 个 SolidWorks 进程正在运行，但无法附着其 COM 会话（HRESULT=0x{(swDiag.LastAttachHResult != 0 ? swDiag.LastAttachHResult : unchecked((int)0x800401E3)):X8}，常见于跨重启旧会话未注册 ROT 或管理员权限不一致；需关闭旧 SolidWorks 窗口后发送“打开当前模型”）",
            SolidWorksSessionState.ConnectedNoActiveDocument =>
                "SolidWorks 已运行并连接，但尚未打开任何模型文档",
            _ => "尚未打开（未检测到运行中的 SolidWorks 进程）"
        };

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
            LocalKnowledgeBaseCatalogSummary: isLocalKnowledgeAvailable ? kbCatalogSummary : null,
            SolidWorksStatusDetail: solidWorksStatusDetail);
    }

    private string BuildWelcomeMessage()
    {
        var profile = DesignBoundaryProfileLoader.Load();
        var v = profile.Vehicle;

        if (_projectStore is not null && ActiveProject is { } active)
        {
            if (active.CurrentVersionNumber > 0)
            {
                return $"已进入【{active.Name}（{active.VersionTag}）】· 车型：{v.VehicleName}（JT/T 1430 分类 {v.StandardModelCode}），当前上弦杆 `{active.UpperChord.SpecText} mm`、下弦杆 `{active.LowerChord.SpecText} mm`。";
            }

            return $"已进入【{active.Name}（{active.VersionTag}）】· 车型：{v.VehicleName}（JT/T 1430 分类 {v.StandardModelCode}，{v.MaterialGrade}，上/下弦杆 `{active.UpperChord.SpecText} mm`），已挂载多标准工程规范知识库。";
        }

        return $"车型：{v.VehicleName}（JT/T 1430 分类 {v.StandardModelCode}，{v.MaterialGrade}，母型 `{v.BaselineWidthMm:0.#}×{v.BaselineHeightMm:0.#}×{v.BaselineThicknessMm:0.#} mm`），已挂载多标准工程规范知识库。";
    }

    private string BuildDynamicGreetingReply()
    {
        var snapshot = BuildRuntimeContextSnapshot();
        var active = ActiveProject;

        var pendingNote = !string.IsNullOrWhiteSpace(snapshot.PendingCadEditSummary)
            ? $"（另外提醒：当前还有一项待确认的改模计划——{snapshot.PendingCadEditSummary}，您可以直接回复“确认修改”或“取消修改”。）"
            : active is { CurrentVersionNumber: > 0 }
                ? $"（当前已定位到 **{active.Name} · {active.VersionTag}** 版本。）"
                : string.Empty;

        return $"""
您好！欢迎使用**桥梁检查车智能设计助手**，当前已为您载入**不可伸缩单层桥梁检查车（JC-GD-NG）**的设计上下文。{pendingNote}我可以协助您完成以下工作：

- **工程规范与附图查证**：检索已挂载的 6 本国标/行标（JT/T 1430、GB/T 3811、GB 50017、GB/T 6728、GB/T 1591、GB 50661）核心条文、公式及高清矢量工程图；
- **结构咨询与方案评估**：结合本车当前截面与受力特点，讨论轻量化、刚度、稳定性、焊缝与选材边界；
- **SolidWorks 参数化改模**：打开当前工作模型，查询或经您确认后联动修改上/下弦杆截面与壁厚；
- **ANSYS APDL 仿真协同**：拉起本地 APDL 启动器界面，辅助梳理校核流程或准备分析脚本。

请问您现在想先从哪一项开始？也可以点击上方「展开设计简报」直接使用快捷入口。
""".Trim();
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
