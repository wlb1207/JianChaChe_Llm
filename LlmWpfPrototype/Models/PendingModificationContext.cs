namespace LlmWpfPrototype.Models;

public sealed class PendingModificationContext
{
    public bool IsActive { get; set; }

    public string? TargetMember { get; set; }

    public string? TargetMemberDisplayName { get; set; }

    public decimal? SectionWidth { get; set; }

    public decimal? SectionHeight { get; set; }

    public decimal? WallThickness { get; set; }

    public string? MissingSlot { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string? SourceUserText { get; set; }
}
