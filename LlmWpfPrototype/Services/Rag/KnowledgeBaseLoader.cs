using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmWpfPrototype.Services.Rag;

public sealed class KnowledgeBaseLoader
{
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

    public string ResolveVerifiedKnowledgeBaseRootPath()
    {
        return ResolveDirectoryPath(VerifiedRulesRelativeDirectory);
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

    public sealed class RagIndexDocument
    {
        public string SourceDocumentPath { get; set; } = string.Empty;

        public DateTime? GeneratedAtUtc { get; set; }

        public List<RagDocumentChunk> Chunks { get; set; } = [];
    }
}
