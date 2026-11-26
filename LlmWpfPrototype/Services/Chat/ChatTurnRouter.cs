using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services.Rag;
using LlmWpfPrototype.Services.Simulation;

namespace LlmWpfPrototype.Services.Chat;

/// <summary>
/// Pure routing & safety classification helpers separating:
/// - Engineering normative/calculation RAG questions
/// - Software/app capability & status questions (handled by unified main LLM with runtime context)
/// - Explicit commands & polite read-only/execution requests
/// </summary>
public static class ChatTurnRouter
{
    private static readonly string[] StrongNormativeTerms =
    [
        "规范", "标准", "校核", "许用", "应力", "安全系数", "强度", "刚度", "挠度",
        "疲劳", "主梁", "屈服", "稳定性", "倾覆", "风载", "载荷组合", "荷载组合",
        "工作级别", "金属结构", "符合要求", "合不合规", "合规", "计算书", "连接校核", "工况"
    ];

    private static readonly string[] SoftwareOrAppTopicTerms =
    [
        "apdl", "ansys", "solidworks", "仿真软件", "仿真程序", "分析软件", "求解器",
        "启动器", "这个软件", "这软件", "程序", "这里面", "可编辑参数", "打开模型",
        "当前模型", "初始模型", "工作模型", "脚本", "代码", "怎么用", "轻量化基线"
    ];

    private static readonly Regex StandardCodeRegex = new(
        @"GB\s*[/／]?\s*T?\s*\d|\d\s*(MPa|kN)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CommandPrefixRegex = new(
        @"^(?:请|帮我|麻烦|我要|我想|给我)*(?:把|将|打开|加载|启动|开启|关闭|修改|改|调整|加厚|保存|查询|读取|查看|扫描|配置|重置|恢复|还原|生成|导出|执行|运行|确认|取消)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool HasStrongNormativeEngineeringSignal(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        return StrongNormativeTerms.Any(term => input.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
               StandardCodeRegex.IsMatch(input);
    }

    public static bool IsSoftwareOrAppTopic(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        return SoftwareOrAppTopicTerms.Any(term => input.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldRouteToKnowledgeChat(
        string input,
        string normalizedInput,
        bool isNegativeOrCancel,
        bool isLiveParameterQuery,
        bool isManualTrussMappingIntent,
        bool isAppCapabilityOrStatusQuestion,
        bool isExplicitModificationCommand,
        bool isApdlLaunchCandidate,
        IReadOnlyList<ConversationMessage> knowledgeHistory,
        string? previousRetrievalTopic)
    {
        if (string.IsNullOrWhiteSpace(input) ||
            isNegativeOrCancel ||
            isLiveParameterQuery ||
            isManualTrussMappingIntent ||
            isAppCapabilityOrStatusQuestion ||
            isExplicitModificationCommand ||
            isApdlLaunchCandidate ||
            IsProjectParameterOrDesignConsultation(input, normalizedInput))
        {
            return false;
        }

        // Software/APDL/SolidWorks questions stay on the unified main LLM unless they explicitly ask a normative/design-check question.
        if (IsSoftwareOrAppTopic(input) && !HasStrongNormativeEngineeringSignal(input))
        {
            return false;
        }

        // Direct imperative execution commands should stay in the CAD/action pipeline unless they are normative questions.
        if (CommandPrefixRegex.IsMatch(normalizedInput) && !HasStrongNormativeEngineeringSignal(input))
        {
            return false;
        }

        if (HasStrongNormativeEngineeringSignal(input))
        {
            return AutomaticKnowledgeChatService.Plan(input, knowledgeHistory, previousRetrievalTopic).NeedsEvidence;
        }

        // Follow-ups only route to RAG when there is an active retrieval topic from a prior knowledge turn.
        if (!string.IsNullOrWhiteSpace(previousRetrievalTopic) && knowledgeHistory.Count > 0)
        {
            return AutomaticKnowledgeChatService.Plan(input, knowledgeHistory, previousRetrievalTopic).NeedsEvidence;
        }

        return false;
    }

    public static bool IsPoliteEditableParameterQuery(string input, string normalizedInput)
    {
        if (string.IsNullOrWhiteSpace(normalizedInput))
        {
            return false;
        }

        var mentionsEditableTarget =
            normalizedInput.Contains("可编辑参数", StringComparison.Ordinal) ||
            normalizedInput.Contains("哪些参数", StringComparison.Ordinal) ||
            normalizedInput.Contains("哪些尺寸", StringComparison.Ordinal) ||
            normalizedInput.Contains("哪些杆件", StringComparison.Ordinal) ||
            normalizedInput.Contains("哪些构件", StringComparison.Ordinal) ||
            normalizedInput.Contains("能改哪些", StringComparison.Ordinal) ||
            normalizedInput.Contains("可以改哪些", StringComparison.Ordinal) ||
            normalizedInput.Contains("能修改哪些", StringComparison.Ordinal) ||
            normalizedInput.Contains("可以修改哪些", StringComparison.Ordinal);

        if (!mentionsEditableTarget)
        {
            return false;
        }

        // Exclude normative engineering questions like "按 GB/T 3811 哪些参数需要校核"
        return !HasStrongNormativeEngineeringSignal(input);
    }

    public static bool IsCurrentStructureDimensionQuestion(string input, string normalizedInput)
    {
        if (string.IsNullOrWhiteSpace(normalizedInput) || HasStrongNormativeEngineeringSignal(input))
        {
            return false;
        }

        var mentionsSizeTerm =
            normalizedInput.Contains("尺寸", StringComparison.Ordinal) ||
            normalizedInput.Contains("截面", StringComparison.Ordinal) ||
            normalizedInput.Contains("壁厚", StringComparison.Ordinal) ||
            normalizedInput.Contains("规格", StringComparison.Ordinal) ||
            normalizedInput.Contains("多大", StringComparison.Ordinal) ||
            normalizedInput.Contains("多少", StringComparison.Ordinal);
        if (!mentionsSizeTerm)
        {
            return false;
        }

        var asksCurrentState =
            normalizedInput.Contains("现在", StringComparison.Ordinal) ||
            normalizedInput.Contains("目前", StringComparison.Ordinal) ||
            normalizedInput.Contains("当前", StringComparison.Ordinal) ||
            normalizedInput.Contains("多大", StringComparison.Ordinal) ||
            normalizedInput.Contains("是多少", StringComparison.Ordinal) ||
            normalizedInput.Contains("什么规格", StringComparison.Ordinal);

        return asksCurrentState;
    }

    public static bool IsProjectParameterOrDesignConsultation(string input, string normalizedInput)
    {
        if (string.IsNullOrWhiteSpace(normalizedInput) || HasStrongNormativeEngineeringSignal(input))
        {
            return false;
        }

        if (IsCurrentStructureDimensionQuestion(input, normalizedInput))
        {
            return true;
        }

        return normalizedInput.Contains("取值范围", StringComparison.Ordinal) ||
               normalizedInput.Contains("范围是多少", StringComparison.Ordinal) ||
               normalizedInput.Contains("什么范围", StringComparison.Ordinal) ||
               normalizedInput.Contains("上限", StringComparison.Ordinal) ||
               normalizedInput.Contains("下限", StringComparison.Ordinal) ||
               normalizedInput.Contains("最大能", StringComparison.Ordinal) ||
               normalizedInput.Contains("最小能", StringComparison.Ordinal) ||
               normalizedInput.Contains("怎么改", StringComparison.Ordinal) ||
               normalizedInput.Contains("如何改", StringComparison.Ordinal) ||
               normalizedInput.Contains("怎么样", StringComparison.Ordinal) ||
               normalizedInput.Contains("行不行", StringComparison.Ordinal) ||
               normalizedInput.Contains("合适吗", StringComparison.Ordinal) ||
               normalizedInput.Contains("合理吗", StringComparison.Ordinal);
    }

    public static bool ShouldPreservePendingCadPlan(
        string input,
        string normalizedInput,
        bool isNegativeOrCancel,
        bool hasExplicitModificationCommand,
        bool isResetRequest)
    {
        if (isNegativeOrCancel || hasExplicitModificationCommand || isResetRequest)
        {
            return false;
        }

        // Questions, capability inquiries, APDL launcher checks, or general discussion while a CAD plan is pending
        // should NOT silently destroy the pending plan.
        if (ApdlLaunchIntentPolicy.IsCandidate(input))
        {
            return true;
        }

        if (input.Contains('？', StringComparison.Ordinal) ||
            input.Contains('?', StringComparison.Ordinal) ||
            normalizedInput.Contains("吗", StringComparison.Ordinal) ||
            normalizedInput.Contains("什么", StringComparison.Ordinal) ||
            normalizedInput.Contains("为什么", StringComparison.Ordinal) ||
            normalizedInput.Contains("怎么", StringComparison.Ordinal) ||
            normalizedInput.Contains("如何", StringComparison.Ordinal) ||
            HasStrongNormativeEngineeringSignal(input) ||
            IsSoftwareOrAppTopic(input))
        {
            return true;
        }

        return false;
    }
}
