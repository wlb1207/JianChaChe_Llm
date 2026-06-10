using System.IO;
using System.Text.RegularExpressions;

namespace LlmWpfPrototype.Services.Rag;

public sealed class KeywordRagService
{
    private const string VerifiedMarkdownIndexVersion = "verified-markdown-v2";
    private static readonly Regex HeadingRegex = new(@"^\s*第.+[章节]\s+.+$", RegexOptions.Compiled);
    private static readonly Regex TokenRegex = new(@"[\u4e00-\u9fa5]{2,}|[A-Za-z0-9/\-\.]{2,}", RegexOptions.Compiled);
    private readonly KnowledgeBaseLoader _knowledgeBaseLoader;

    public KeywordRagService(KnowledgeBaseLoader knowledgeBaseLoader)
    {
        _knowledgeBaseLoader = knowledgeBaseLoader;
    }

    public bool LastBuiltFromVerifiedMarkdown { get; private set; }

    public int LastVerifiedMarkdownChunkCount { get; private set; }

    public bool LastFallbackToKnowledgeBaseText { get; private set; }

    public IReadOnlyList<RagDocumentChunk> EnsureIndex()
    {
        LastBuiltFromVerifiedMarkdown = false;
        LastVerifiedMarkdownChunkCount = 0;
        LastFallbackToKnowledgeBaseText = false;

        if (_knowledgeBaseLoader.TryLoadVerifiedRuleDocuments(out var verifiedDocuments, out _))
        {
            var loadedVerifiedDocuments = verifiedDocuments
                .Where(item => item.MarkdownLoaded && !string.IsNullOrWhiteSpace(item.Markdown))
                .ToList();

            if (loadedVerifiedDocuments.Count > 0)
            {
                var verifiedSourcePath = _knowledgeBaseLoader.ResolveVerifiedKnowledgeBaseRootPath();
                var sourceDocumentPath = $"{verifiedSourcePath}|{VerifiedMarkdownIndexVersion}";
                var existingVerifiedIndex = _knowledgeBaseLoader.LoadIndexOrEmpty();
                if (existingVerifiedIndex.Chunks.Count > 0 &&
                    string.Equals(existingVerifiedIndex.SourceDocumentPath, sourceDocumentPath, StringComparison.OrdinalIgnoreCase))
                {
                    LastBuiltFromVerifiedMarkdown = existingVerifiedIndex.Chunks.Any(chunk => !string.IsNullOrWhiteSpace(chunk.RuleId));
                    LastVerifiedMarkdownChunkCount = existingVerifiedIndex.Chunks.Count(chunk => !string.IsNullOrWhiteSpace(chunk.RuleId));
                    LastFallbackToKnowledgeBaseText = false;
                    return existingVerifiedIndex.Chunks;
                }

                var verifiedChunks = BuildVerifiedRuleChunks(loadedVerifiedDocuments);
                _knowledgeBaseLoader.SaveIndex(new KnowledgeBaseLoader.RagIndexDocument
                {
                    SourceDocumentPath = sourceDocumentPath,
                    GeneratedAtUtc = DateTime.UtcNow,
                    Chunks = verifiedChunks.ToList()
                });

                LastBuiltFromVerifiedMarkdown = true;
                LastVerifiedMarkdownChunkCount = verifiedChunks.Count;
                LastFallbackToKnowledgeBaseText = false;
                return verifiedChunks;
            }
        }

        LastFallbackToKnowledgeBaseText = true;
        if (!_knowledgeBaseLoader.TryLoadKnowledgeBaseText(out var text, out _))
        {
            _knowledgeBaseLoader.EnsureRagIndexFile();
            return [];
        }

        var existingIndex = _knowledgeBaseLoader.LoadIndexOrEmpty();
        var sourcePath = _knowledgeBaseLoader.ResolveKnowledgeBaseTextPath();
        if (existingIndex.Chunks.Count > 0 &&
            string.Equals(existingIndex.SourceDocumentPath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            LastBuiltFromVerifiedMarkdown = existingIndex.Chunks.Any(chunk => !string.IsNullOrWhiteSpace(chunk.RuleId));
            LastVerifiedMarkdownChunkCount = existingIndex.Chunks.Count(chunk => !string.IsNullOrWhiteSpace(chunk.RuleId));
            return existingIndex.Chunks;
        }

        _knowledgeBaseLoader.TryLoadKeyRules(out var rules, out _);
        var chunks = BuildChunks(text, Path.GetFileName(sourcePath), rules);
        _knowledgeBaseLoader.SaveIndex(new KnowledgeBaseLoader.RagIndexDocument
        {
            SourceDocumentPath = sourcePath,
            GeneratedAtUtc = DateTime.UtcNow,
            Chunks = chunks.ToList()
        });

        return chunks;
    }

    public IReadOnlyList<RagSearchResult> Search(string query, int topK)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
        {
            return [];
        }

        var chunks = EnsureIndex();
        var queryTokens = BuildQueryTokens(query).ToList();
        if (queryTokens.Count == 0)
        {
            return [];
        }

        return chunks
            .Select(chunk => ScoreChunk(chunk, queryTokens))
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Chunk.Order)
            .Take(topK)
            .ToList();
    }

    private static IReadOnlyList<RagDocumentChunk> BuildChunks(
        string text,
        string sourceDocument,
        IReadOnlyList<KnowledgeBaseLoader.KnowledgeRuleItem> rules)
    {
        var paragraphs = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chunks = new List<RagDocumentChunk>();
        var currentSection = string.Empty;
        var order = 0;

        foreach (var paragraph in paragraphs)
        {
            if (HeadingRegex.IsMatch(paragraph))
            {
                currentSection = paragraph.Trim();
            }

            var content = paragraph.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var keywords = Tokenize(content)
                .Concat(ExtractRuleKeywords(content, rules))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            chunks.Add(new RagDocumentChunk
            {
                Id = $"chunk-{order + 1:D4}",
                SourceDocument = sourceDocument,
                Title = string.IsNullOrWhiteSpace(currentSection) ? sourceDocument : currentSection,
                Section = currentSection,
                Text = content,
                Order = order++,
                Keywords = keywords
            });
        }

        return chunks;
    }

    private static IReadOnlyList<RagDocumentChunk> BuildVerifiedRuleChunks(
        IReadOnlyList<KnowledgeBaseLoader.VerifiedRuleDocument> verifiedDocuments)
    {
        var chunks = new List<RagDocumentChunk>();
        var order = 0;

        foreach (var document in verifiedDocuments)
        {
            var rule = document.Rule;
            var markdown = document.Markdown.Trim();
            if (string.IsNullOrWhiteSpace(markdown))
            {
                continue;
            }

            var keywords = BuildRuleSearchTerms(rule, markdown)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            chunks.Add(new RagDocumentChunk
            {
                Id = $"verified-rule-{order + 1:D4}",
                RuleId = rule.Id,
                SourceDocument = rule.File,
                Title = !string.IsNullOrWhiteSpace(rule.Topic) ? rule.Topic : rule.Title,
                Topic = rule.Topic,
                Clause = rule.Clause,
                Summary = rule.Summary,
                Section = rule.Clause,
                Text = markdown,
                Order = order++,
                Keywords = keywords,
                MustRequire = rule.MustRequire.ToList(),
                DoNotAnswerWithout = rule.DoNotAnswerWithout.ToList(),
                KeyLimits = rule.KeyLimits.ToList(),
                KeyFormulas = rule.KeyFormulas.ToList()
            });
        }

        return chunks;
    }

    private static IEnumerable<string> ExtractRuleKeywords(
        string content,
        IReadOnlyList<KnowledgeBaseLoader.KnowledgeRuleItem> rules)
    {
        foreach (var rule in rules)
        {
            if (rule.Keywords.Any(keyword => content.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var keyword in rule.Keywords)
                {
                    yield return keyword;
                }

                if (!string.IsNullOrWhiteSpace(rule.Topic))
                {
                    yield return rule.Topic;
                }

                if (!string.IsNullOrWhiteSpace(rule.Title))
                {
                    yield return rule.Title;
                }

                if (!string.IsNullOrWhiteSpace(rule.Clause))
                {
                    yield return rule.Clause;
                }

                if (!string.IsNullOrWhiteSpace(rule.Summary))
                {
                    foreach (var token in Tokenize(rule.Summary))
                    {
                        yield return token;
                    }
                }

                foreach (var item in rule.MustRequire)
                {
                    yield return item;
                }

                foreach (var item in rule.DoNotAnswerWithout)
                {
                    yield return item;
                }

                foreach (var item in rule.KeyLimits)
                {
                    yield return item;
                }

                foreach (var item in rule.KeyFormulas)
                {
                    yield return item;
                }
            }
        }
    }

    private static RagSearchResult ScoreChunk(RagDocumentChunk chunk, IReadOnlyList<string> queryTokens)
    {
        var matchedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double score = 0;

        foreach (var token in queryTokens)
        {
            if (chunk.Text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                matchedKeywords.Add(token);
                score += 3;
            }

            if (!string.IsNullOrWhiteSpace(chunk.Title) &&
                chunk.Title.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                matchedKeywords.Add(token);
                score += 3;
            }

            if (!string.IsNullOrWhiteSpace(chunk.Topic) &&
                chunk.Topic.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                matchedKeywords.Add(token);
                score += 3;
            }

            if (!string.IsNullOrWhiteSpace(chunk.Summary) &&
                chunk.Summary.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                matchedKeywords.Add(token);
                score += 2;
            }

            if (!string.IsNullOrWhiteSpace(chunk.Clause) &&
                chunk.Clause.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                matchedKeywords.Add(token);
                score += 1;
            }

            if (chunk.Keywords.Any(keyword => string.Equals(keyword, token, StringComparison.OrdinalIgnoreCase)))
            {
                matchedKeywords.Add(token);
                score += 2.5;
            }

            if (chunk.MustRequire.Any(item => item.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                matchedKeywords.Add(token);
                score += 1.5;
            }

            if (chunk.DoNotAnswerWithout.Any(item => item.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                matchedKeywords.Add(token);
                score += 1.2;
            }

            if (chunk.KeyLimits.Any(item => item.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                matchedKeywords.Add(token);
                score += 1.5;
            }

            if (chunk.KeyFormulas.Any(item => item.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                matchedKeywords.Add(token);
                score += 1.5;
            }
        }

        return new RagSearchResult
        {
            Chunk = chunk,
            Score = score,
            MatchedKeywords = matchedKeywords.ToList(),
            FromVerifiedMarkdown = !string.IsNullOrWhiteSpace(chunk.RuleId)
        };
    }

    private static IEnumerable<string> BuildQueryTokens(string query)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in Tokenize(query))
        {
            if (seen.Add(token))
            {
                yield return token;
            }

            if (token.Length > 4)
            {
                for (var length = 2; length <= Math.Min(6, token.Length); length++)
                {
                    for (var index = 0; index <= token.Length - length; index++)
                    {
                        var ngram = token.Substring(index, length);
                        if (seen.Add(ngram))
                        {
                            yield return ngram;
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<string> BuildRuleSearchTerms(
        KnowledgeBaseLoader.KnowledgeRuleItem rule,
        string markdown)
    {
        foreach (var keyword in rule.Keywords)
        {
            yield return keyword;
        }

        if (!string.IsNullOrWhiteSpace(rule.Topic))
        {
            yield return rule.Topic;
        }

        if (!string.IsNullOrWhiteSpace(rule.Title))
        {
            yield return rule.Title;
        }

        if (!string.IsNullOrWhiteSpace(rule.Clause))
        {
            yield return rule.Clause;
        }

        if (!string.IsNullOrWhiteSpace(rule.Summary))
        {
            foreach (var token in Tokenize(rule.Summary))
            {
                yield return token;
            }
        }

        foreach (var item in rule.MustRequire)
        {
            yield return item;
        }

        foreach (var item in rule.DoNotAnswerWithout)
        {
            yield return item;
        }

        foreach (var item in rule.KeyLimits)
        {
            yield return item;
        }

        foreach (var item in rule.KeyFormulas)
        {
            yield return item;
        }

        foreach (var token in Tokenize(markdown))
        {
            yield return token;
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        return TokenRegex.Matches(text)
            .Select(match => match.Value.Trim())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
