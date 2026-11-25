using System.Text;

namespace LlmWpfPrototype.Services.Chat;

public sealed record ActionAuditEntry(
    DateTime Timestamp,
    string ActionName,
    string Summary,
    bool Succeeded);

public sealed record AssistantRuntimeContextSnapshot(
    bool IsSolidWorksModelOpened,
    string ActiveDocumentName,
    bool IsWorkspaceInitialized,
    IReadOnlyList<string> EditableMemberSummaries,
    bool IsApdlLauncherConfigured,
    bool IsApdlLauncherAvailable,
    bool IsApdlSolverConfigured,
    bool IsApdlSolverAvailable,
    bool IsLightweightStudyConfigured,
    bool IsLocalKnowledgeBaseAvailable,
    string? PendingCadEditSummary,
    bool PendingResetConfirmation,
    string? PendingSlotSummary,
    string? LastModifiedParameterSummary,
    int ApdlLauncherStartCount,
    DateTime? LastApdlLauncherStartedAt,
    IReadOnlyList<ActionAuditEntry> RecentActions)
{
    public string ToPromptBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("【当前程序实时能力与运行状态（事实依据，禁止编造未配置能力或隐瞒已配置能力）】");

        var modelState = IsSolidWorksModelOpened
            ? $"已打开（文档：{(string.IsNullOrWhiteSpace(ActiveDocumentName) ? "当前活动模型" : ActiveDocumentName)}）"
            : "尚未打开";
        sb.AppendLine($"- SolidWorks 模型状态：{modelState}；工作区已初始化：{(IsWorkspaceInitialized ? "是" : "否")}");

        if (EditableMemberSummaries.Count > 0)
        {
            sb.AppendLine($"- 已配置可编辑杆件：{string.Join("；", EditableMemberSummaries)}（修改尺寸均需用户确认后再写入 SolidWorks）");
        }
        else
        {
            sb.AppendLine("- 已配置可编辑杆件：当前未读取到启用项");
        }

        var launcherState = IsApdlLauncherAvailable
            ? "已配置且本机可用（可直接响应“打开 APDL / 打开仿真软件”启动本地启动器界面；仅启动界面，不自动运行求解）"
            : IsApdlLauncherConfigured
                ? "配置了路径但本机未找到 launcher.exe"
                : "未配置";
        sb.AppendLine($"- 本地 ANSYS APDL 启动器：{launcherState}");

        var solverState = IsApdlSolverAvailable
            ? "已配置且本机可用"
            : IsApdlSolverConfigured
                ? "配置了路径但本机未找到可执行文件"
                : "未配置";
        var studyState = IsLightweightStudyConfigured
            ? "已配置（可发送“运行轻量化仿真”执行批处理对比）"
            : "尚未配置算例文件（当前可先发送“读取轻量化基线”核对模型尺寸，或启动本地 APDL 界面/起草脚本，暂不可一键自动批处理求解）";
        sb.AppendLine($"- ANSYS 批处理求解器：{solverState}；自动轻量化算例：{studyState}");

        if (ApdlLauncherStartCount > 0)
        {
            var timeText = LastApdlLauncherStartedAt.HasValue
                ? $"，最近于 {LastApdlLauncherStartedAt.Value:HH:mm:ss}"
                : string.Empty;
            sb.AppendLine($"- 本会话 APDL 启动器记录：已启动 {ApdlLauncherStartCount} 次{timeText}");
        }

        sb.AppendLine($"- 本地工程规范知识库（GB/T 3811-2008）：{(IsLocalKnowledgeBaseAvailable ? "已接入（规范校核类问题会自动检索并附依据引用）" : "未检测到")}");

        if (!string.IsNullOrWhiteSpace(LastModifiedParameterSummary))
        {
            sb.AppendLine($"- 最近一次成功修改：{LastModifiedParameterSummary}");
        }

        if (!string.IsNullOrWhiteSpace(PendingCadEditSummary))
        {
            sb.AppendLine($"- 当前有待确认的尺寸修改计划：{PendingCadEditSummary}（等待用户发送“确认修改”或“取消修改”，尚未写入 SolidWorks）");
        }

        if (PendingResetConfirmation)
        {
            sb.AppendLine("- 当前有待确认的工作区重置请求（等待用户发送“确认重置并打开初始模型”）");
        }

        if (!string.IsNullOrWhiteSpace(PendingSlotSummary))
        {
            sb.AppendLine($"- 当前有待补全的修改槽位：{PendingSlotSummary}");
        }

        if (RecentActions.Count > 0)
        {
            sb.AppendLine("- 本会话最近执行记录：");
            foreach (var entry in RecentActions)
            {
                var status = entry.Succeeded ? "成功" : "未完成/失败";
                sb.AppendLine($"  * [{entry.Timestamp:HH:mm:ss}] {entry.ActionName}：{entry.Summary}（{status}）");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
