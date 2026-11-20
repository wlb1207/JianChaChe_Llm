using System.IO;

namespace LlmWpfPrototype.Services.Rag;

public sealed record LocalKnowledgeHit(
    int Rank, string Title, string SourceDocument, string Clause, string ChunkId,
    double Score, string FullText, string MatchedKeywords)
{
    public IReadOnlyList<string> EvidenceTerms { get; init; } = [];
    public string Heading => $"命中 {Rank} · {Title}";
    public string Metadata => $"文件：{SourceDocument} | 条款（资料标注）：{(string.IsNullOrWhiteSpace(Clause) ? "未提供" : Clause)} | chunk_id：{ChunkId}";
    public string ScoreLabel => $"关键词检索评分：{Score:0.##}（仅用于排序，不是答案正确率）";
    public string VerificationLabel => "页码：未确认；转录与条款对应关系：未经人工核验；命中片段不等于规范结论已核验。";
}

public sealed record LocalKnowledgeSearchResponse(
    string Status, string Coverage, IReadOnlyList<LocalKnowledgeHit> Hits, bool HasError);

/// <summary>Only local files and existing keyword retrieval; no model or business execution dependencies.</summary>
public sealed class LocalKnowledgeSearchService
{
    private readonly KnowledgeBaseLoader _loader;
    private readonly Gbt3811RagService _rag;

    public LocalKnowledgeSearchService(KnowledgeBaseLoader loader)
    {
        _loader = loader;
        _rag = new Gbt3811RagService(loader);
    }

    public static LocalKnowledgeSearchService CreateForApplication() => new(new KnowledgeBaseLoader());

    public LocalKnowledgeSearchResponse Search(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new("请输入检索问题或关键词。", "", [], false);
        try
        {
            var root = _loader.ResolveVerifiedKnowledgeBaseRootPath();
            var allMarkdown = Directory.Exists(root)
                ? Directory.GetFiles(root, "*.md", SearchOption.AllDirectories) : [];
            var coverage = "";
            var evidenceVocabulary = new List<string>();
            if (File.Exists(_loader.ResolveKeyRulesPath()))
            {
                if (!_loader.TryLoadVerifiedRuleDocuments(out var docs, out var error))
                    return new($"资料读取失败：{error}", "", [], true);
                evidenceVocabulary.AddRange(docs.SelectMany(d => d.Rule.Keywords));
                var missing = docs.Where(x => !x.MarkdownLoaded).Select(x => x.Rule.File).ToList();
                if (missing.Count > 0)
                    return new($"资料缺失或不可读取：{string.Join("、", missing)}。已停止，不使用旧索引冒充完整资料。", "", [], true);
                var included = docs.Select(d => d.Rule.File.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var omitted = allMarkdown.Select(x => Path.GetRelativePath(root, x).Replace('\\', '/'))
                    .Where(x => !included.Contains(x)).OrderBy(x => x).ToList();
                coverage = $"规则数：{docs.Count}；可读取规则 Markdown：{docs.Count(x => x.MarkdownLoaded)}；磁盘 Markdown：{allMarkdown.Length}。" +
                    $"优先检索规则列出的 Markdown；TXT 仅作原有回退，不与这些片段混合检索。" +
                    (omitted.Count == 0 ? "" : $" 未列入规则的文件：{string.Join("、", omitted)}。");
            }
            else
            {
                if (!File.Exists(_loader.ResolveKnowledgeBaseTextPath()))
                    return new("资料缺失：未找到规则文件或 TXT。未找到依据。", "", [], true);
                coverage = "规则文件缺失，使用现有 TXT 回退分段检索；条款可能未提供，未确认页码。";
            }

            var results = _rag.Search(question, 5);
            if (!string.IsNullOrWhiteSpace(_rag.LastErrorMessage))
                return new(_rag.LastErrorMessage, coverage, [], true);
            var index = _loader.LoadIndexOrEmpty();
            coverage += $" 本次索引：{index.Chunks.Count} 个片段；内容指纹已参与缓存校验。";
            if (_rag.LastFallbackToKnowledgeBaseText)
                coverage += " 本次实际使用 TXT 回退；不是全文与规则的联合检索。";
            var evidenceTerms = LocalKnowledgeEvidenceSelector.QueryTerms(question, evidenceVocabulary);
            var hits = results.Select((r, i) => new LocalKnowledgeHit(i + 1, r.Chunk.Title,
                r.Chunk.SourceDocument, r.Chunk.Clause, r.Chunk.Id, r.Score,
                r.Chunk.Text, string.Join("、", r.MatchedKeywords)) { EvidenceTerms = evidenceTerms }).ToList();
            return new(hits.Count == 0
                ? "未找到依据；未调用模型补答案。"
                : $"实际检索命中 {hits.Count} 个片段。仅展示本地资料，不生成答案或执行操作。",
                coverage, hits, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new($"本地资料检索失败：{ex.Message}", "", [], true);
        }
    }
}
