namespace LlmWpfPrototype.Services.Rag;

public sealed class Gbt3811RagService
{
    private readonly KnowledgeBaseLoader _knowledgeBaseLoader;
    private readonly KeywordRagService _keywordRagService;
    private readonly RagPromptBuilder _ragPromptBuilder;

    public Gbt3811RagService()
        : this(new KnowledgeBaseLoader())
    {
    }

    public Gbt3811RagService(KnowledgeBaseLoader knowledgeBaseLoader)
    {
        _knowledgeBaseLoader = knowledgeBaseLoader;
        _keywordRagService = new KeywordRagService(_knowledgeBaseLoader);
        _ragPromptBuilder = new RagPromptBuilder();
    }

    public string LastErrorMessage { get; private set; } = string.Empty;

    public bool LastBuiltFromVerifiedMarkdown => _keywordRagService.LastBuiltFromVerifiedMarkdown;

    public int LastVerifiedMarkdownChunkCount => _keywordRagService.LastVerifiedMarkdownChunkCount;

    public bool LastFallbackToKnowledgeBaseText => _keywordRagService.LastFallbackToKnowledgeBaseText;

    public IReadOnlyList<RagSearchResult> Search(string query, int topK)
    {
        LastErrorMessage = string.Empty;

        var hasVerifiedRules = _knowledgeBaseLoader.TryLoadKeyRuleDocument(out _, out _);
        var hasLegacyText = _knowledgeBaseLoader.TryLoadKnowledgeBaseText(out _, out var errorMessage);
        if (!hasVerifiedRules && !hasLegacyText)
        {
            LastErrorMessage = errorMessage;
            _knowledgeBaseLoader.EnsureRagIndexFile();
            return [];
        }

        try
        {
            return _keywordRagService.Search(query, topK);
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"GB/T 3811 检索失败：{ex.Message}";
            return [];
        }
    }

    public string BuildPrompt(string query, int topK)
    {
        var results = Search(query, topK);
        _knowledgeBaseLoader.TryLoadKeyRuleDocument(out var ruleDocument, out _);
        return _ragPromptBuilder.BuildPrompt(query, results, ruleDocument);
    }
}
