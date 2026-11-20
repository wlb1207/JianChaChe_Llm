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
         "主梁", "桁架", "弦杆", "截面", "载荷", "荷载", "材料", "边界", "参数", "尺寸", "壁厚", "反力架", "桥梁", "屈服", "稳定性"];
    private static readonly string[] FollowupTerms = ["这个", "那个", "该", "它", "这些", "那么", "为什么", "依据呢", "参数呢", "继续", "呢", "how about", "what about"];
    private static readonly string[] FocusTerms = ["许用应力", "安全系数", "强度", "刚度", "挠度", "疲劳", "稳定性", "载荷", "荷载", "材料", "壁厚", "截面"];
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
        || Regex.IsMatch(question, @"GB\s*[/／]?\s*T?\s*\d|\d\s*(MPa|kN|mm)", RegexOptions.IgnoreCase);

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
            : selected.Sources.Count == 0 ? "not_found: 当前本地资料未找到相关内容；只能作一般性说明，不能肯定项目规范数值或合规结论"
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
        // Even with zero evidence, only the configured model produces the final answer.
        KnowledgeDisclosureGuard.Validate(prepared.Request.Prompt);
        var answer = await _model.AnswerFromLocalKnowledgeAsync(prepared.Request.Prompt, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var mapped = LocalKnowledgeChatService.CompleteAnswer(prepared.Request, answer);
        if (mapped.Sources.Count == 0 && mapped.InvalidIds.Count == 0)
            mapped = mapped with { CitationStatus = prepared.Plan.NeedsEvidence
                ? "本轮未提供可引用资料；一般性解释不等于已核验的项目规范结论。" : "" };
        return mapped with {
            UserProvenance = KnowledgeDisclosureGuard.HistoryFor(prepared.Request.Prompt, question),
            AnswerProvenance = KnowledgeDisclosureGuard.HistoryFor(prepared.Request.Prompt, answer)
        };
    }
}
