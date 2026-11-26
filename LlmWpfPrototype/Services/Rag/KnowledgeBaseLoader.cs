using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LlmWpfPrototype.Services.Rag;

public sealed class KnowledgeBaseLoader
{
    private const string KnowledgeBaseRootRelativeDirectory = @"KnowledgeBase";
    private const string LegacyGbt3811PackId = "GB_T_3811_2008";
    private const string KnowledgeBaseTextRelativePath = @"KnowledgeBase\GB_T_3811_2008\gb_t_3811_2008.txt";
    private const string KeyRulesRelativePath = @"KnowledgeBase\GB_T_3811_2008\gb_t_3811_key_rules.json";
    private const string VerifiedRulesRelativeDirectory = @"KnowledgeBase\GB_T_3811_2008";
    private const string RagIndexRelativePath = @"Data\rag_index.json";
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string LastErrorMessage { get; private set; } = string.Empty;

    public string ResolveKnowledgeBaseTextPath()
    {
        return ResolveFilePath(KnowledgeBaseTextRelativePath);
    }

    public string ResolveKeyRulesPath()
    {
        return ResolveFilePath(KeyRulesRelativePath);
    }

    public string ResolveKnowledgeBaseRootPath()
    {
        return ResolveDirectoryPath(KnowledgeBaseRootRelativeDirectory);
    }

    public string ResolveVerifiedKnowledgeBaseRootPath()
    {
        return ResolveDirectoryPath(VerifiedRulesRelativeDirectory);
    }

    public bool TryLoadAllKnowledgeDocuments(
        out IReadOnlyList<MultiStandardRuleDocument> documents,
        out IReadOnlyList<KnowledgePackSummary> packs,
        out string contentFingerprint,
        out string errorMessage)
    {
        documents = [];
        packs = [];
        contentFingerprint = string.Empty;
        errorMessage = string.Empty;

        var kbRoot = ResolveKnowledgeBaseRootPath();
        if (!Directory.Exists(kbRoot))
        {
            errorMessage = $"未找到知识库根目录：{KnowledgeBaseRootRelativeDirectory}";
            LastErrorMessage = errorMessage;
            return false;
        }

        var discoveredDirs = Directory.GetDirectories(kbRoot)
            .Select(path => new DirectoryInfo(path))
            .Where(dir => !dir.Name.StartsWith('.'))
            .OrderBy(dir => string.Equals(dir.Name, LegacyGbt3811PackId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(dir => dir.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var loadedDocs = new List<MultiStandardRuleDocument>();
        var loadedPacks = new List<KnowledgePackSummary>();
        var fingerprintLines = new List<string>();
        var errors = new List<string>();

        foreach (var packDir in discoveredDirs)
        {
            var packId = packDir.Name;
            var isLegacy3811 = string.Equals(packId, LegacyGbt3811PackId, StringComparison.OrdinalIgnoreCase);
            var keyRulesPath = FindPackKeyRulesFile(packDir.FullName);
            var referencedRelFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var packTopics = new List<string>();
            var standardCode = packId;
            var standardName = packId;
            var description = string.Empty;
            var ruleCount = 0;

            if (!string.IsNullOrWhiteSpace(keyRulesPath) && File.Exists(keyRulesPath))
            {
                try
                {
                    var rulesBytes = File.ReadAllBytes(keyRulesPath);
                    fingerprintLines.Add($"RULES|{packId}/{Path.GetFileName(keyRulesPath)}|{ComputeSha256Hex(rulesBytes)}");
                    var rulesJson = Encoding.UTF8.GetString(rulesBytes).TrimStart('\uFEFF');
                    var ruleDoc = JsonSerializer.Deserialize<KnowledgeRuleDocument>(rulesJson, _serializerOptions)
                        ?? new KnowledgeRuleDocument();
                    if (!string.IsNullOrWhiteSpace(ruleDoc.Standard)) standardCode = ruleDoc.Standard.Trim();
                    if (!string.IsNullOrWhiteSpace(ruleDoc.Name)) standardName = ruleDoc.Name.Trim();
                    description = ruleDoc.Description ?? string.Empty;

                    var rules = ruleDoc.Rules.Count > 0 ? ruleDoc.Rules : ruleDoc.SampleRules;
                    var ruleOrdinal = 0;
                    foreach (var rule in rules)
                    {
                        var relFile = (rule.File ?? string.Empty).Replace('\\', '/').TrimStart('/');
                        if (string.IsNullOrWhiteSpace(relFile))
                        {
                            continue;
                        }

                        ruleOrdinal++;
                        referencedRelFiles.Add(relFile);
                        var fullMdPath = Path.Combine(packDir.FullName, relFile.Replace('/', Path.DirectorySeparatorChar));
                        var markdown = string.Empty;
                        var loaded = false;
                        if (File.Exists(fullMdPath))
                        {
                            var mdBytes = File.ReadAllBytes(fullMdPath);
                            fingerprintLines.Add($"MD|{packId}/{relFile}|{ComputeSha256Hex(mdBytes)}");
                            markdown = Encoding.UTF8.GetString(mdBytes).TrimStart('\uFEFF');
                            loaded = !string.IsNullOrWhiteSpace(markdown);
                        }

                        var chunkId = isLegacy3811
                            ? $"verified-rule-{ruleOrdinal:D4}"
                            : $"{packId.ToLowerInvariant()}-rule-{ruleOrdinal:D4}";
                        var sourceDocument = isLegacy3811 ? relFile : $"{packId}/{relFile}";

                        loadedDocs.Add(new MultiStandardRuleDocument
                        {
                            PackId = packId,
                            Standard = standardCode,
                            StandardName = standardName,
                            SourceDocument = sourceDocument,
                            ChunkId = chunkId,
                            Rule = rule,
                            Markdown = markdown,
                            MarkdownLoaded = loaded,
                            IsLooseMarkdown = false
                        });

                        if (loaded)
                        {
                            ruleCount++;
                            var topic = !string.IsNullOrWhiteSpace(rule.Topic) ? rule.Topic : rule.Title;
                            if (!string.IsNullOrWhiteSpace(topic))
                            {
                                packTopics.Add(string.IsNullOrWhiteSpace(rule.Clause) ? topic : $"{topic}（{rule.Clause}）");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{packId}: 读取规则索引失败（{ex.Message}）");
                }
            }

            // Dynamic extensibility: automatically discover loose .md documents added in any pack folder
            // (excluding README files and already-indexed rule files).
            var looseCount = 0;
            var mdFiles = Directory.GetFiles(packDir.FullName, "*.md", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            foreach (var mdFile in mdFiles)
            {
                var fileName = Path.GetFileName(mdFile);
                if (fileName.StartsWith("README", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relFile = Path.GetRelativePath(packDir.FullName, mdFile).Replace('\\', '/');
                if (referencedRelFiles.Contains(relFile))
                {
                    continue;
                }

                // Skip unindexed legacy GB_T_3811_2008 loose markdowns (e.g. 12_rag_answer_rules.md) to preserve
                // exact legacy GB/T 3811 coverage & ranking behavior.
                if (isLegacy3811)
                {
                    continue;
                }

                try
                {
                    var mdBytes = File.ReadAllBytes(mdFile);
                    var markdown = Encoding.UTF8.GetString(mdBytes).TrimStart('\uFEFF').Trim();
                    if (string.IsNullOrWhiteSpace(markdown))
                    {
                        continue;
                    }

                    looseCount++;
                    fingerprintLines.Add($"LOOSE|{packId}/{relFile}|{ComputeSha256Hex(mdBytes)}");
                    var syntheticRule = BuildSyntheticRuleFromMarkdown(packId, standardCode, relFile, markdown, looseCount);
                    loadedDocs.Add(new MultiStandardRuleDocument
                    {
                        PackId = packId,
                        Standard = string.IsNullOrWhiteSpace(syntheticRule.Title) ? standardCode : ExtractFrontMatterValue(markdown, "Source") ?? standardCode,
                        StandardName = standardName,
                        SourceDocument = $"{packId}/{relFile}",
                        ChunkId = $"{packId.ToLowerInvariant()}-doc-{looseCount:D4}",
                        Rule = syntheticRule,
                        Markdown = markdown,
                        MarkdownLoaded = true,
                        IsLooseMarkdown = true
                    });
                    packTopics.Add(syntheticRule.Topic);
                }
                catch
                {
                    // Skip unreadable custom file without breaking other packs.
                }
            }

            if (ruleCount > 0 || looseCount > 0)
            {
                loadedPacks.Add(new KnowledgePackSummary
                {
                    PackId = packId,
                    Standard = standardCode,
                    Name = standardName,
                    Description = description,
                    RuleCount = ruleCount,
                    LooseMarkdownCount = looseCount,
                    RulesFilePath = keyRulesPath ?? string.Empty,
                    Topics = packTopics
                });
            }
        }

        if (loadedDocs.Count == 0)
        {
            errorMessage = errors.Count > 0
                ? string.Join("；", errors)
                : "知识库目录下未发现可用的规则或 Markdown 知识文档。";
            LastErrorMessage = errorMessage;
            return false;
        }

        fingerprintLines.Sort(StringComparer.Ordinal);
        contentFingerprint = ComputeSha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", fingerprintLines)))[..16];
        documents = loadedDocs;
        packs = loadedPacks;
        errorMessage = errors.Count > 0 ? string.Join("；", errors) : string.Empty;
        LastErrorMessage = errorMessage;
        return true;
    }

    private static string? FindPackKeyRulesFile(string packDirectory)
    {
        var keyRuleFiles = Directory.GetFiles(packDirectory, "*key_rules.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (keyRuleFiles.Count > 0)
        {
            return keyRuleFiles[0];
        }

        var fallback = Path.Combine(packDirectory, "rules.json");
        return File.Exists(fallback) ? fallback : null;
    }

    private static KnowledgeRuleItem BuildSyntheticRuleFromMarkdown(
        string packId,
        string defaultStandard,
        string relFile,
        string markdown,
        int ordinal)
    {
        var headingMatch = Regex.Match(markdown, @"(?m)^#\s+(.+)$");
        var topic = ExtractFrontMatterValue(markdown, "Topic")
            ?? (headingMatch.Success ? headingMatch.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(relFile));
        var clause = ExtractFrontMatterValue(markdown, "Clause") ?? string.Empty;
        var source = ExtractFrontMatterValue(markdown, "Source") ?? defaultStandard;
        var keywords = new List<string> { source, topic };
        if (!string.IsNullOrWhiteSpace(clause))
        {
            keywords.Add(clause);
        }

        foreach (Match subHeading in Regex.Matches(markdown, @"(?m)^#{2,3}\s+(.+)$"))
        {
            var text = subHeading.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                keywords.Add(text);
            }
        }

        return new KnowledgeRuleItem
        {
            Id = $"{packId}-DOC-{ordinal:D2}",
            Title = topic,
            Topic = topic,
            File = relFile,
            Clause = clause,
            Summary = $"{source}：{topic}",
            Keywords = keywords.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static string? ExtractFrontMatterValue(string markdown, string key)
    {
        var match = Regex.Match(markdown, $@"(?mi)^{Regex.Escape(key)}\s*:\s*(.+)$");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public string ResolveRagIndexPath()
    {
        return Path.Combine(AppPaths.DataDirectory, "rag_index.json");
    }

    public bool TryLoadKnowledgeBaseText(out string text, out string errorMessage)
    {
        text = string.Empty;
        var textPath = ResolveKnowledgeBaseTextPath();
        if (!File.Exists(textPath))
        {
            errorMessage = $"未找到知识库文本文件：{KnowledgeBaseTextRelativePath}";
            LastErrorMessage = errorMessage;
            return false;
        }

        text = File.ReadAllText(textPath);
        errorMessage = string.Empty;
        LastErrorMessage = string.Empty;
        return true;
    }

    public bool TryLoadKeyRules(out IReadOnlyList<KnowledgeRuleItem> rules, out string errorMessage)
    {
        rules = [];
        LastLoadedRuleDocument = null;
        var rulesPath = ResolveKeyRulesPath();
        if (!File.Exists(rulesPath))
        {
            errorMessage = $"未找到规则文件：{KeyRulesRelativePath}";
            LastErrorMessage = errorMessage;
            return false;
        }

        try
        {
            var json = File.ReadAllText(rulesPath);
            var document = JsonSerializer.Deserialize<KnowledgeRuleDocument>(json, _serializerOptions) ?? new KnowledgeRuleDocument();
            LastLoadedRuleDocument = document;
            rules = document.Rules.Count > 0
                ? document.Rules
                : document.SampleRules;
            errorMessage = string.Empty;
            LastErrorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"读取规则文件失败：{ex.Message}";
            LastErrorMessage = errorMessage;
            return false;
        }
    }

    public bool TryLoadKeyRuleDocument(out KnowledgeRuleDocument document, out string errorMessage)
    {
        document = new KnowledgeRuleDocument();
        var rulesPath = ResolveKeyRulesPath();
        if (!File.Exists(rulesPath))
        {
            errorMessage = $"未找到规则文件：{KeyRulesRelativePath}";
            LastErrorMessage = errorMessage;
            LastLoadedRuleDocument = null;
            return false;
        }

        try
        {
            var json = File.ReadAllText(rulesPath);
            document = JsonSerializer.Deserialize<KnowledgeRuleDocument>(json, _serializerOptions) ?? new KnowledgeRuleDocument();
            LastLoadedRuleDocument = document;
            errorMessage = string.Empty;
            LastErrorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"读取规则文件失败：{ex.Message}";
            LastErrorMessage = errorMessage;
            LastLoadedRuleDocument = null;
            return false;
        }
    }

    public bool TryLoadRuleMarkdown(KnowledgeRuleItem rule, out string markdown)
    {
        markdown = string.Empty;
        if (rule is null || string.IsNullOrWhiteSpace(rule.File))
        {
            LastErrorMessage = "规则文件路径为空。";
            return false;
        }

        return TryLoadRuleMarkdown(rule.File, out markdown);
    }

    public bool TryLoadRuleMarkdown(string relativeFile, out string markdown)
    {
        markdown = string.Empty;
        if (string.IsNullOrWhiteSpace(relativeFile))
        {
            LastErrorMessage = "规则文件路径为空。";
            return false;
        }

        try
        {
            var fullPath = ResolveVerifiedRuleFilePath(relativeFile);
            if (!File.Exists(fullPath))
            {
                LastErrorMessage = $"未找到规则 Markdown 文件：{relativeFile}";
                return false;
            }

            markdown = File.ReadAllText(fullPath);
            LastErrorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"读取规则 Markdown 文件失败：{ex.Message}";
            return false;
        }
    }

    public bool TryLoadVerifiedRuleDocuments(out IReadOnlyList<VerifiedRuleDocument> documents, out string errorMessage)
    {
        documents = [];
        if (!TryLoadKeyRules(out var rules, out errorMessage))
        {
            return false;
        }

        var results = new List<VerifiedRuleDocument>();
        foreach (var rule in rules)
        {
            TryLoadRuleMarkdown(rule, out var markdown);
            results.Add(new VerifiedRuleDocument
            {
                Rule = rule,
                Markdown = markdown,
                MarkdownLoaded = !string.IsNullOrWhiteSpace(markdown)
            });
        }

        documents = results;
        errorMessage = string.Empty;
        LastErrorMessage = string.Empty;
        return true;
    }

    public string EnsureRagIndexFile()
    {
        var indexPath = ResolveRagIndexPath();
        var directory = Path.GetDirectoryName(indexPath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);

        if (!File.Exists(indexPath))
        {
            var emptyIndex = new RagIndexDocument();
            File.WriteAllText(indexPath, JsonSerializer.Serialize(emptyIndex, _serializerOptions));
        }

        return indexPath;
    }

    public RagIndexDocument LoadIndexOrEmpty()
    {
        var indexPath = EnsureRagIndexFile();
        try
        {
            var json = File.ReadAllText(indexPath);
            return JsonSerializer.Deserialize<RagIndexDocument>(json, _serializerOptions) ?? new RagIndexDocument();
        }
        catch
        {
            return new RagIndexDocument();
        }
    }

    public void SaveIndex(RagIndexDocument indexDocument)
    {
        var indexPath = EnsureRagIndexFile();
        File.WriteAllText(indexPath, JsonSerializer.Serialize(indexDocument, _serializerOptions));
    }

    public string? TryFindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var hasProjectFile = current.GetFiles("*.csproj").Any();
            var hasWpfMarkers =
                File.Exists(Path.Combine(current.FullName, "App.xaml")) &&
                File.Exists(Path.Combine(current.FullName, "MainWindow.xaml"));

            if (hasProjectFile && hasWpfMarkers)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private string ResolveFilePath(string relativePath)
    {
        var directBasePath = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (File.Exists(directBasePath))
        {
            return directBasePath;
        }

        var projectRoot = TryFindProjectRoot();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var projectRootPath = Path.Combine(projectRoot, relativePath);
            if (File.Exists(projectRootPath))
            {
                return projectRootPath;
            }
        }

        return directBasePath;
    }

    private string ResolveDirectoryPath(string relativePath)
    {
        var directBasePath = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (Directory.Exists(directBasePath))
        {
            return directBasePath;
        }

        var projectRoot = TryFindProjectRoot();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var projectRootPath = Path.Combine(projectRoot, relativePath);
            if (Directory.Exists(projectRootPath))
            {
                return projectRootPath;
            }
        }

        return directBasePath;
    }

    private string ResolveVerifiedRuleFilePath(string relativeFile)
    {
        var normalizedRelativeFile = relativeFile
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        return Path.Combine(ResolveVerifiedKnowledgeBaseRootPath(), normalizedRelativeFile);
    }

    public KnowledgeRuleDocument? LastLoadedRuleDocument { get; private set; }

    public sealed class KnowledgeRuleDocument
    {
        [JsonPropertyName("standard")]
        public string Standard { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("language")]
        public string Language { get; set; } = string.Empty;

        [JsonPropertyName("verified")]
        public bool Verified { get; set; }

        [JsonPropertyName("source_type")]
        public string SourceType { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("knowledge_base_path")]
        public string KnowledgeBasePath { get; set; } = string.Empty;

        [JsonPropertyName("global_answer_policy")]
        public GlobalAnswerPolicy GlobalAnswerPolicy { get; set; } = new();

        [JsonPropertyName("rules")]
        public List<KnowledgeRuleItem> Rules { get; set; } = [];

        [JsonPropertyName("sampleRules")]
        public List<KnowledgeRuleItem> SampleRules { get; set; } = [];
    }

    public sealed class KnowledgeRuleItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("topic")]
        public string Topic { get; set; } = string.Empty;

        [JsonPropertyName("clause")]
        public string Clause { get; set; } = string.Empty;

        [JsonPropertyName("keywords")]
        public List<string> Keywords { get; set; } = [];

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("must_require")]
        public List<string> MustRequire { get; set; } = [];

        [JsonPropertyName("do_not_answer_without")]
        public List<string> DoNotAnswerWithout { get; set; } = [];

        [JsonPropertyName("key_limits")]
        public List<string> KeyLimits { get; set; } = [];

        [JsonPropertyName("key_formulas")]
        public List<string> KeyFormulas { get; set; } = [];
    }

    public sealed class GlobalAnswerPolicy
    {
        [JsonPropertyName("if_parameters_missing")]
        public string IfParametersMissing { get; set; } = string.Empty;

        [JsonPropertyName("if_safety_related")]
        public string IfSafetyRelated { get; set; } = string.Empty;

        [JsonPropertyName("do_not_fabricate")]
        public List<string> DoNotFabricate { get; set; } = [];

        [JsonPropertyName("distinguish_checks")]
        public List<string> DistinguishChecks { get; set; } = [];
    }

    public sealed class VerifiedRuleDocument
    {
        public KnowledgeRuleItem Rule { get; set; } = new();

        public string Markdown { get; set; } = string.Empty;

        public bool MarkdownLoaded { get; set; }
    }

    public sealed class MultiStandardRuleDocument
    {
        public string PackId { get; set; } = string.Empty;

        public string Standard { get; set; } = string.Empty;

        public string StandardName { get; set; } = string.Empty;

        public string SourceDocument { get; set; } = string.Empty;

        public string ChunkId { get; set; } = string.Empty;

        public KnowledgeRuleItem Rule { get; set; } = new();

        public string Markdown { get; set; } = string.Empty;

        public bool MarkdownLoaded { get; set; }

        public bool IsLooseMarkdown { get; set; }
    }

    public sealed class KnowledgePackSummary
    {
        public string PackId { get; set; } = string.Empty;

        public string Standard { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public int RuleCount { get; set; }

        public int LooseMarkdownCount { get; set; }

        public string RulesFilePath { get; set; } = string.Empty;

        public List<string> Topics { get; set; } = [];
    }

    public sealed class RagIndexDocument
    {
        public string SourceDocumentPath { get; set; } = string.Empty;

        public DateTime? GeneratedAtUtc { get; set; }

        public List<RagDocumentChunk> Chunks { get; set; } = [];
    }
}
