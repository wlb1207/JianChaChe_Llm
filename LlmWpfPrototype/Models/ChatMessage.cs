namespace LlmWpfPrototype.Models;

public sealed class ChatMessage
{
    public bool IsUser { get; set; }

    public string RoleDisplay { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public bool IsLocalKnowledgeReply { get; set; }

    public IReadOnlyList<LlmWpfPrototype.Services.Rag.LocalChatEvidence> LocalSources { get; set; } = [];

    public string LocalCitationStatus { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string TimeDisplay => Timestamp.ToString("HH:mm");
}
