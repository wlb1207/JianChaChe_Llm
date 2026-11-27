using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services.Rag;

public sealed record AutomaticChatPlan(bool NeedsEvidence, string RetrievalQuery,
    IReadOnlyList<ConversationMessage> Context, string? Topic);
public sealed record AutomaticChatPreparation(AutomaticChatPlan Plan,
    LocalChatEvidenceRequest Request, string EvidenceStatus);

// A single text-generation path for normal conversation, including automatic evidence.
// No command dispatcher, business parser, COM, APDL or filesystem-writing service is available here.
public sealed class AutomaticKnowledgeChatService
{
    private static readonly string[] EngineeringTerms =
        ["工程", "设计", "规范", "标准", "校核", "计算", "许用", "应力", "安全系数", "强度", "刚度", "挠度", "疲劳",
         "主梁", "桁架", "弦杆", "截面", "载荷", "荷载", "材料", "边界", "参数", "尺寸", "壁厚", "反力架", "桥梁", "屈服", "稳定性",
         "宽厚比", "长细比", "焊缝", "焊脚", "螺栓", "法兰", "节点", "护栏", "踢脚板", "抗风", "防腐蚀", "残余变形", "检查车",
         "轮压", "车轮", "打滑", "黏着", "试验", "静载", "动载", "最小壁厚", "最小厚度", "脆性", "防脆断", "质量组别", "冲击功", "预拱", "预翘", "限界", "净距",
         "冷弯", "方管", "矩管", "方矩管", "圆弧半径", "圆角", "壁厚偏差", "惯性矩", "截面模数", "回转半径", "惯性半径", "理论重量", "扭转度",
         "Q355", "Q390", "Q420", "碳当量", "CEV", "抗拉强度", "伸长率", "交货状态", "夏比", "镀锌",
         "坡口", "全焊透", "部分焊透", "计算厚度", "两面角", "咬边", "未焊满", "外观质量", "超声波", "焊接检验", "搭接",
         "焊接", "规范图", "附图", "插图", "示意图", "构造图", "节点图", "断面图", "详图", "图集"];
    private static readonly string[] FollowupTerms = ["这个", "那个", "该", "它", "这些", "那么", "为什么", "依据呢", "参数呢", "继续", "呢", "how about", "what about"];
    private static readonly string[] FocusTerms = ["许用应力", "安全系数", "强度", "刚度", "挠度", "疲劳", "稳定性", "载荷", "荷载", "材料", "壁厚", "截面", "宽厚比", "长细比", "焊缝", "节点", "抗风", "护栏", "轮压", "试验", "防脆断", "净距", "预拱", "方管", "矩管", "截面特性", "惯性矩", "圆角", "壁厚偏差", "碳当量", "冲击功", "屈服强度", "镀锌", "坡口", "焊脚", "计算厚度", "外观质量", "焊接检验", "规范图", "附图", "插图", "示意图", "构造图", "断面图"];
    private readonly Func<string, LocalKnowledgeSearchResponse> _search;
    private readonly ILlmService _model;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public AutomaticKnowledgeChatService(Func<string, LocalKnowledgeSearchResponse> search, ILlmService model)
    { _search = search; _model = model; }

    public static AutomaticChatPlan Plan(string question, IReadOnlyList<ConversationMessage> history, string? previousTopic = null)
    {
        var context = new List<ConversationMessage>();
        var remaining = 2000;
        foreach (var item in history.Reverse())
        {
            if (context.Count == 4 || remaining <= 0) break;
            if (item.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(item.Content)) continue;
            var content = item.Content[..Math.Min(Math.Min(item.Content.Length, 700), remaining)];
            context.Insert(0, new ConversationMessage { Role = item.Role, Content = content, KnowledgeProvenance = item.KnowledgeProvenance });
            remaining -= content.Length;
        }
        var anchor = previousTopic
            ?? context.LastOrDefault(x => x.Role == "user" && IsEngineering(x.Content)
                && !FollowupTerms.Any(term => x.Content.Contains(term, StringComparison.OrdinalIgnoreCase)))?.Content
            ?? context.LastOrDefault(x => x.Role == "user" && IsEngineering(x.Content))?.Content;
        var followup = FollowupTerms.Any(x => question.Contains(x, StringComparison.OrdinalIgnoreCase));
        var hasFocus = FocusTerms.Any(x => question.Contains(x, StringComparison.OrdinalIgnoreCase));
        var needs = IsEngineering(question) || (followup && anchor is not null);
        var query = followup && !hasFocus && anchor is not null ? question + "\n上文问题：" + anchor : question;
        query = query[..Math.Min(query.Length, LocalKnowledgeChatService.MaxQuestionCharacters)];
        var topic = IsEngineering(question) && (!followup || hasFocus) ? question : anchor;
        return new(needs, query, followup ? context : [], needs ? topic : null);
    }

    private static bool IsEngineering(string question) => EngineeringTerms.Any(x => question.Contains(x, StringComparison.OrdinalIgnoreCase))
        || Regex.IsMatch(question, @"(?:GB|JT|JTG|JB|NB|YB)\s*[/／]?\s*T?\s*[A-Z]?\d|\d\s*(MPa|kN|mm)", RegexOptions.IgnoreCase);

    public async Task<AutomaticChatPreparation> PrepareAsync(string question,
        IReadOnlyList<ConversationMessage> history, CancellationToken cancellationToken = default, string? previousTopic = null)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > LocalKnowledgeChatService.MaxQuestionCharacters)
            throw new ArgumentException("请输入 1 至 2000 字符的问题。");
        var plan = Plan(question, history, previousTopic);
        IReadOnlyList<LocalKnowledgeHit> hits = [];
        if (plan.NeedsEvidence)
        {
            var search = await Task.Run(() => _search(plan.RetrievalQuery), cancellationToken);
            if (search.HasError) throw new InvalidOperationException("本地资料检索失败；本轮未请求模型，不使用旧资料或模板冒充回答。");
            hits = search.Hits;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var selected = LocalKnowledgeChatService.BuildRequest(question, hits, plan.RetrievalQuery, 10400);
        foreach (var source in selected.Sources)
        {
            if (Path.IsPathRooted(source.Source.SourceDocument)
                || source.Source.SourceDocument.Split('/', '\\').Contains("..")
                || Regex.IsMatch(source.SentText, @"[A-Za-z]:[\\/]|\\\\[^\s\\]+\\|(?i)(api[_ -]?key|authorization|bearer|password|token)\s*[:=]\s*\S+|sk-[A-Za-z0-9]{12,}"))
                throw new InvalidOperationException("选中资料含本机路径或疑似凭据；已停止，未发送。");
        }
        var status = !plan.NeedsEvidence ? "not_requested: 普通闲聊，无需工程资料"
            : selected.Sources.Count == 0 ? "not_found: 当前本地资料未找到可引用的相关依据"
            : "available: 仅本轮选中片段；适用性及版本优先级未确认；存在冲突须指出并追问";
        using var evidence = JsonDocument.Parse(selected.Prompt);
        var prompt = JsonSerializer.Serialize(new {
            question, conversation_context = plan.Context.Select(x => new { role = x.Role, content = x.Content }),
            retrieval_topic = plan.Topic,
            evidence_status = status, evidence = evidence.RootElement.GetProperty("evidence")
        }, Json);
        if (prompt.Length > LocalKnowledgeChatService.MaxPromptCharacters)
            throw new InvalidOperationException("本轮上下文超过长度预算；未发送。");
        KnowledgeDisclosureGuard.Register(prompt, selected.Sources, plan.Context);
        return new(plan, new(prompt, selected.Sources), status);
    }

    public async Task<LocalKnowledgeChatResult> AnswerAsync(string question,
        IReadOnlyList<ConversationMessage> history, CancellationToken cancellationToken = default, string? previousTopic = null)
    {
        var prepared = await PrepareAsync(question, history, cancellationToken, previousTopic);
        cancellationToken.ThrowIfCancellationRequested();
        if (prepared.Plan.NeedsEvidence && prepared.Request.Sources.Count == 0)
        {
            KnowledgeDisclosureGuard.Validate(prepared.Request.Prompt);
            const string insufficiencyAnswer = "现有资料不足，暂不能给出可靠的设计判断或推荐数值。请先说明要设计或校核的构件，以及已知的使用工况；不需要先知道规范编号。";
            return new LocalKnowledgeChatResult(insufficiencyAnswer,
                "未找到可引用的本地依据，本轮未调用模型。", [], [], [])
            {
                UserProvenance = KnowledgeDisclosureGuard.HistoryFor(prepared.Request.Prompt, question),
                AnswerProvenance = KnowledgeDisclosureGuard.HistoryFor(prepared.Request.Prompt, insufficiencyAnswer)
            };
        }

        KnowledgeDisclosureGuard.Validate(prepared.Request.Prompt);
        var answer = await _model.AnswerFromLocalKnowledgeAsync(prepared.Request.Prompt, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var mapped = LocalKnowledgeChatService.CompleteAnswer(prepared.Request, answer);
        if (prepared.Plan.NeedsEvidence && (mapped.CitedIds.Count == 0 || mapped.InvalidIds.Count > 0))
            return mapped with
            {
                Answer = "本轮模型回答未提供可核对的引用，暂不展示工程结论。可展开下方资料核对原文。"
            };
        if (mapped.Sources.Count == 0 && mapped.InvalidIds.Count == 0)
            mapped = mapped with { CitationStatus = prepared.Plan.NeedsEvidence
                ? "本轮未提供可引用资料；一般性解释不等于已核验的项目规范结论。" : "" };
        return mapped with {
            UserProvenance = KnowledgeDisclosureGuard.HistoryFor(prepared.Request.Prompt, question),
            AnswerProvenance = KnowledgeDisclosureGuard.HistoryFor(prepared.Request.Prompt, answer)
        };
    }
}
