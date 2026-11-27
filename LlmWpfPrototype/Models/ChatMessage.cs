using LlmWpfPrototype.Services.Rag;

namespace LlmWpfPrototype.Models;

public sealed class ChatMessage
{
    public bool IsUser { get; set; }

    public string RoleDisplay { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public bool IsLocalKnowledgeReply { get; set; }

    public bool IsSystemWelcomeAnchor { get; set; }

    public IReadOnlyList<LocalChatEvidence> LocalSources { get; set; } = [];

    public IReadOnlyList<ChatImageAttachment> AttachedImages { get; set; } = [];

    public bool HasAttachedImages => AttachedImages.Count > 0;

    public bool HasVisibleText => !string.IsNullOrWhiteSpace(Text);

    public string LocalCitationStatus { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string TimeDisplay => Timestamp.ToString("HH:mm");

    /// <summary>
    /// High-resolution vector engineering figures extracted from this turn's retrieved local sources,
    /// deduplicated against any figures already embedded inline inside <see cref="Text"/> so users
    /// always see the relevant diagrams directly in the reply bubble without expanding collapsed panels.
    /// </summary>
    public IReadOnlyList<LocalKnowledgeFigureItem> DirectReplyFigures
    {
        get
        {
            if (LocalSources.Count == 0)
            {
                return [];
            }

            var inlinePaths = LocalChatEvidence.ExtractFigures(Text, string.Empty, string.Empty)
                .Select(f => f.RelativeAssetPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var results = new List<LocalKnowledgeFigureItem>();
            var seen = new HashSet<string>(inlinePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var source in LocalSources)
            {
                foreach (var fig in source.Figures)
                {
                    if (seen.Add(fig.RelativeAssetPath))
                    {
                        results.Add(fig);
                    }
                }
            }

            return results;
        }
    }

    public bool HasDirectReplyFigures => DirectReplyFigures.Count > 0;

    public string DirectReplyFiguresHeader => DirectReplyFigures.Count > 0
        ? $"本轮关联高清规范附图（共 {DirectReplyFigures.Count} 张 · 点击图片可放大缩放）"
        : string.Empty;
}
