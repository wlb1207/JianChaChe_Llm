using System.IO;

namespace LlmWpfPrototype.Services.Rag;

public sealed record LocalKnowledgeHit(
    int Rank, string Title, string SourceDocument, string Clause, string ChunkId,
    double Score, string FullText, string MatchedKeywords)
{
    public string Standard { get; init; } = "GB/T 3811-2008";
    public IReadOnlyList<string> EvidenceTerms { get; init; } = [];
    public string Heading => string.IsNullOrWhiteSpace(Standard)
        ? $"命中 {Rank} · {Title}"
        : $"命中 {Rank} · [{Standard}] {Title}";
    public string Metadata => $"标准：{Standard} | 文件：{SourceDocument} | 条款（资料标注）：{(string.IsNullOrWhiteSpace(Clause) ? "未提供" : Clause)} | chunk_id：{ChunkId}";
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

    public string BuildKnowledgeBaseCatalogSummary()
    {
        if (!_loader.TryLoadAllKnowledgeDocuments(out var allDocs, out var packs, out _, out _) || packs.Count == 0)
        {
            return "未检测到可用的本地工程规范知识库。";
        }

        var figureCountsByPack = allDocs
            .GroupBy(d => d.PackId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(d => LocalChatEvidence.ExtractFigures(d.Markdown, d.Standard, string.Empty))
                    .Select(f => f.RelativeAssetPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                StringComparer.OrdinalIgnoreCase);

        var parts = packs.Select(pack =>
        {
            var countLabel = pack.LooseMarkdownCount > 0
                ? $"{pack.RuleCount} 项规则 + {pack.LooseMarkdownCount} 份扩展文档"
                : $"{pack.RuleCount} 项规则";
            var figCount = figureCountsByPack.TryGetValue(pack.PackId, out var c) ? c : 0;
            var figSuffix = figCount > 0 ? $"，含 {figCount} 张高清规范图" : string.Empty;
            return $"{pack.Standard}《{pack.Name}》（{countLabel}{figSuffix}）";
        });

        var totalFigures = DiscoverAllEmbeddedFigures().Count;
        var summary = string.Join("；", parts);
        return totalFigures > 0
            ? $"{summary}；全库共内置 {totalFigures} 张高清矢量工程图（含本车 JC-GD-NG 联动断面图，可直接对话展示或在「规范图库」中浏览放大）。"
            : summary;
    }

    public IReadOnlyList<LocalKnowledgeFigureItem> DiscoverAllEmbeddedFigures()
    {
        var results = new List<LocalKnowledgeFigureItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kbRoot = _loader.ResolveKnowledgeBaseRootPath();

        // 1) Vehicle linkage diagram first if present
        const string vehicleRel = "JC_GD_NG/assets/fig_jc_gd_ng_chord_linkage.png";
        var vehicleFull = Path.Combine(kbRoot, vehicleRel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(vehicleFull) && seenPaths.Add(vehicleRel))
        {
            results.Add(new LocalKnowledgeFigureItem(
                "本车 JC-GD-NG 主桁架弦杆截面与横向红管补偿联动断面图",
                vehicleRel,
                "本车 JC-GD-NG",
                string.Empty));
        }

        // 2) All figures embedded in loaded verified knowledge documents
        if (_loader.TryLoadAllKnowledgeDocuments(out var allDocs, out _, out _, out _))
        {
            foreach (var doc in allDocs)
            {
                if (!doc.MarkdownLoaded || string.IsNullOrWhiteSpace(doc.Markdown))
                {
                    continue;
                }

                foreach (var fig in LocalChatEvidence.ExtractFigures(doc.Markdown, doc.Standard, string.Empty))
                {
                    var fullPath = Path.Combine(kbRoot, fig.RelativeAssetPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(fullPath) && seenPaths.Add(fig.RelativeAssetPath))
                    {
                        results.Add(fig);
                    }
                }
            }
        }

        return results;
    }

    public LocalKnowledgeSearchResponse Search(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new("请输入检索问题或关键词。", "", [], false);
        try
        {
            var coverage = "";
            var evidenceVocabulary = new List<string>();
            if (_loader.TryLoadAllKnowledgeDocuments(out var allDocs, out var packs, out var fingerprint, out _))
            {
                var missing = allDocs
                    .Where(x => !x.IsLooseMarkdown && !x.MarkdownLoaded)
                    .Select(x => x.SourceDocument)
                    .ToList();
                if (missing.Count > 0)
                    return new($"资料缺失或不可读取：{string.Join("、", missing)}。已停止，不使用旧索引冒充完整资料。", "", [], true);

                evidenceVocabulary.AddRange(allDocs.SelectMany(d => d.Rule.Keywords));
                evidenceVocabulary.AddRange(allDocs.Select(d => d.Standard).Where(s => !string.IsNullOrWhiteSpace(s)));
                evidenceVocabulary.AddRange(allDocs.Select(d => KeywordRagService.NormalizeStandardBaseName(d.StandardName)).Where(s => !string.IsNullOrWhiteSpace(s)));
                evidenceVocabulary.AddRange(allDocs.Select(d => d.Rule.Topic).Where(s => !string.IsNullOrWhiteSpace(s)));

                var packDetails = string.Join("、", packs.Select(p =>
                    p.LooseMarkdownCount > 0
                        ? $"{p.Standard}({p.RuleCount}规则+{p.LooseMarkdownCount}扩展)"
                        : $"{p.Standard}({p.RuleCount}规则)"));
                coverage = $"多标准知识库已加载 {packs.Count} 个知识包：{packDetails}；目录指纹 {fingerprint}。新增/修改文件会自动重建索引。";
            }
            else if (File.Exists(_loader.ResolveKeyRulesPath()))
            {
                if (!_loader.TryLoadVerifiedRuleDocuments(out var docs, out var error))
                    return new($"资料读取失败：{error}", "", [], true);
                evidenceVocabulary.AddRange(docs.SelectMany(d => d.Rule.Keywords));
                var missing = docs.Where(x => !x.MarkdownLoaded).Select(x => x.Rule.File).ToList();
                if (missing.Count > 0)
                    return new($"资料缺失或不可读取：{string.Join("、", missing)}。已停止，不使用旧索引冒充完整资料。", "", [], true);
                coverage = $"规则数：{docs.Count}；可读取规则 Markdown：{docs.Count(x => x.MarkdownLoaded)}。";
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
                r.Chunk.Text, string.Join("、", r.MatchedKeywords))
            {
                Standard = string.IsNullOrWhiteSpace(r.Chunk.Standard) ? "GB/T 3811-2008" : r.Chunk.Standard,
                EvidenceTerms = evidenceTerms
            }).ToList();
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
