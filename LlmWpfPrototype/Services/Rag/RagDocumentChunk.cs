namespace LlmWpfPrototype.Services.Rag;

public sealed class RagDocumentChunk
{
    public string Id { get; set; } = string.Empty;

    public string SourceDocument { get; set; } = string.Empty;

    public string RuleId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public string Clause { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Section { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public int Order { get; set; }

    public List<string> Keywords { get; set; } = [];

    public List<string> MustRequire { get; set; } = [];

    public List<string> DoNotAnswerWithout { get; set; } = [];

    public List<string> KeyLimits { get; set; } = [];

    public List<string> KeyFormulas { get; set; } = [];
}
