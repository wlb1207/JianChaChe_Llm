namespace LlmWpfPrototype.Services.Rag;

public sealed class RagSearchResult
{
    public RagDocumentChunk Chunk { get; set; } = new();

    public double Score { get; set; }

    public List<string> MatchedKeywords { get; set; } = [];

    public bool FromVerifiedMarkdown { get; set; }
}
