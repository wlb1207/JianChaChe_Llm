namespace LlmWpfPrototype.Models;

public sealed class ConversationMessage
{
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
