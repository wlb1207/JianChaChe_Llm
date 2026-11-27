using System.Globalization;
using System.Text.Json.Serialization;
using LlmWpfPrototype.Services.Chat;
using LlmWpfPrototype.Services.Design;

namespace LlmWpfPrototype.Models;

public sealed class MemberSectionSpecRecord
{
    public string MemberId { get; set; } = string.Empty;

    public string MemberName { get; set; } = string.Empty;

    public decimal WidthMm { get; set; } = 70m;

    public decimal HeightMm { get; set; } = 70m;

    public decimal ThicknessMm { get; set; } = 5m;

    public string Source { get; set; } = "母型基准";

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public RectangularTubeMechanicsResult Mechanics =>
        SectionMechanicsCalculator.CalculateRectangularTube(WidthMm, HeightMm, ThicknessMm);

    [JsonIgnore]
    public string SpecText =>
        $"{WidthMm.ToString("0.##", CultureInfo.InvariantCulture)}×{HeightMm.ToString("0.##", CultureInfo.InvariantCulture)}×{ThicknessMm.ToString("0.##", CultureInfo.InvariantCulture)}";

    [JsonIgnore]
    public string HudDisplayText => $"{SpecText} mm";

    public MemberSectionSpecRecord Clone() => new()
    {
        MemberId = MemberId,
        MemberName = MemberName,
        WidthMm = WidthMm,
        HeightMm = HeightMm,
        ThicknessMm = ThicknessMm,
        Source = Source,
        UpdatedAt = UpdatedAt
    };

    public static MemberSectionSpecRecord CreateBaseline(string memberId, string memberName) => new()
    {
        MemberId = memberId,
        MemberName = memberName,
        WidthMm = 70m,
        HeightMm = 70m,
        ThicknessMm = 5m,
        Source = "母型基准",
        UpdatedAt = DateTime.Now
    };
}

public sealed class DesignVersionCheckpoint
{
    public int VersionNumber { get; set; }

    public string VersionTag => $"V{VersionNumber}";

    public string Summary { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public MemberSectionSpecRecord UpperChord { get; set; } = MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆");

    public MemberSectionSpecRecord LowerChord { get; set; } = MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆");

    public MemberSectionSpecRecord LowerTruss { get; set; } = MemberSectionSpecRecord.CreateBaseline("lower_truss", "下桁架");

    public long CadExecutionElapsedMs { get; set; }

    [JsonIgnore]
    public string TimeDisplay => CreatedAt.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string SpecsOverviewText =>
        $"上弦杆 {UpperChord.SpecText} | 下弦杆 {LowerChord.SpecText}";

    public DesignVersionCheckpoint Clone() => new()
    {
        VersionNumber = VersionNumber,
        Summary = Summary,
        CreatedAt = CreatedAt,
        UpperChord = UpperChord.Clone(),
        LowerChord = LowerChord.Clone(),
        LowerTruss = LowerTruss.Clone(),
        CadExecutionElapsedMs = CadExecutionElapsedMs
    };
}

public sealed class PersistedLocalEvidence
{
    public string Id { get; set; } = string.Empty;
    public string Standard { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SourceDocument { get; set; } = string.Empty;
    public string Clause { get; set; } = string.Empty;
    public string ChunkId { get; set; } = string.Empty;
    public string SentText { get; set; } = string.Empty;
    public string FullText { get; set; } = string.Empty;

    public LlmWpfPrototype.Services.Rag.LocalChatEvidence ToRuntimeEvidence(int rank) =>
        new(
            Id,
            new LlmWpfPrototype.Services.Rag.LocalKnowledgeHit(
                rank,
                Title,
                SourceDocument,
                Clause,
                ChunkId,
                0d,
                string.IsNullOrEmpty(FullText) ? SentText : FullText,
                string.Empty)
            {
                Standard = string.IsNullOrWhiteSpace(Standard) ? "本地规范" : Standard
            },
            SentText);

    public static PersistedLocalEvidence FromRuntimeEvidence(LlmWpfPrototype.Services.Rag.LocalChatEvidence evidence) => new()
    {
        Id = evidence.Id,
        Standard = evidence.Source.Standard,
        Title = evidence.Source.Title,
        SourceDocument = evidence.Source.SourceDocument,
        Clause = evidence.Source.Clause,
        ChunkId = evidence.Source.ChunkId,
        SentText = evidence.SentText,
        FullText = evidence.FullText
    };
}

public sealed class PersistedChatImageAttachment
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string MimeType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public long ByteSize { get; set; }

    public ChatImageAttachment ToRuntimeAttachment() => new()
    {
        Id = Id,
        FileName = FileName,
        RelativePath = RelativePath,
        MimeType = string.IsNullOrWhiteSpace(MimeType) ? "image/png" : MimeType,
        Width = Width,
        Height = Height,
        ByteSize = ByteSize
    };

    public static PersistedChatImageAttachment FromRuntimeAttachment(ChatImageAttachment attachment) => new()
    {
        Id = attachment.Id,
        FileName = attachment.FileName,
        RelativePath = attachment.RelativePath,
        MimeType = attachment.MimeType,
        Width = attachment.Width,
        Height = attachment.Height,
        ByteSize = attachment.ByteSize
    };
}

public sealed class PersistedChatMessage
{
    public bool IsUser { get; set; }

    public string RoleDisplay { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public bool IsLocalKnowledgeReply { get; set; }

    public bool IsSystemWelcomeAnchor { get; set; }

    public string LocalCitationStatus { get; set; } = string.Empty;

    public List<PersistedLocalEvidence> LocalSources { get; set; } = [];

    public List<PersistedChatImageAttachment> AttachedImages { get; set; } = [];

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public ChatMessage ToRuntimeChatMessage() => new()
    {
        IsUser = IsUser,
        RoleDisplay = RoleDisplay,
        Text = Text,
        IsLocalKnowledgeReply = IsLocalKnowledgeReply,
        IsSystemWelcomeAnchor = IsSystemWelcomeAnchor || IsLegacySystemWelcomeText(IsUser, IsLocalKnowledgeReply, Text),
        LocalCitationStatus = LocalCitationStatus,
        LocalSources = LocalSources is { Count: > 0 }
            ? LocalSources.Select((s, idx) => s.ToRuntimeEvidence(idx + 1)).ToList()
            : [],
        AttachedImages = AttachedImages is { Count: > 0 }
            ? AttachedImages.Select(a => a.ToRuntimeAttachment()).ToList()
            : [],
        Timestamp = Timestamp
    };

    public static PersistedChatMessage FromRuntimeChatMessage(ChatMessage message) => new()
    {
        IsUser = message.IsUser,
        RoleDisplay = message.RoleDisplay,
        Text = message.Text,
        IsLocalKnowledgeReply = message.IsLocalKnowledgeReply,
        IsSystemWelcomeAnchor = message.IsSystemWelcomeAnchor,
        LocalCitationStatus = message.LocalCitationStatus,
        LocalSources = message.LocalSources is { Count: > 0 }
            ? message.LocalSources.Select(PersistedLocalEvidence.FromRuntimeEvidence).ToList()
            : [],
        AttachedImages = message.AttachedImages is { Count: > 0 }
            ? message.AttachedImages.Select(PersistedChatImageAttachment.FromRuntimeAttachment).ToList()
            : [],
        Timestamp = message.Timestamp
    };

    private static bool IsLegacySystemWelcomeText(bool isUser, bool isLocalKnowledge, string? text)
    {
        if (isUser || isLocalKnowledge || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.StartsWith("已进入【", StringComparison.Ordinal) ||
               text.StartsWith("您好，我是桥梁检查车智能设计助手", StringComparison.Ordinal) ||
               text.StartsWith("已绑定实车", StringComparison.Ordinal);
    }
}

public sealed class PersistedTurnRecord
{
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public ConversationTurnKind Kind { get; set; } = ConversationTurnKind.Chat;

    public string UserContent { get; set; } = string.Empty;

    public string AssistantContent { get; set; } = string.Empty;
}

public sealed class PersistedActionRecord
{
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string ActionName { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public bool Succeeded { get; set; }
}

public sealed class DesignConversationSummary
{
    public string Id { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;

    public string Title { get; set; } = "新对话";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public int MessageCount { get; set; }

    [JsonIgnore]
    public bool IsSelected { get; set; }

    [JsonIgnore]
    public string UpdatedTimeShort =>
        UpdatedAt.Date == DateTime.Today ? UpdatedAt.ToString("HH:mm") : UpdatedAt.ToString("MM-dd");

    public DesignConversationSummary Clone() => new()
    {
        Id = Id,
        ProjectId = ProjectId,
        Title = Title,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        MessageCount = MessageCount
    };
}

public sealed class DesignConversationRecord
{
    public string Id { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;

    public string Title { get; set; } = "新对话";

    public bool IsTitleAutoGenerated { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string DraftInput { get; set; } = string.Empty;

    public List<PersistedChatMessage> Messages { get; set; } = [];

    public List<PersistedTurnRecord> Turns { get; set; } = [];

    public List<PersistedActionRecord> Actions { get; set; } = [];
}

public sealed class DesignProjectMetrics
{
    public int TotalUserTurns { get; set; }

    public int ConfirmedCadModifications { get; set; }

    public int SafetyInterceptions { get; set; }
}

public sealed class DesignProjectRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = "默认检查车设计项目";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public bool IsPinned { get; set; }

    public bool IsArchived { get; set; }

    public bool IsDefaultProject { get; set; }

    public string WorkspaceFolder { get; set; } = string.Empty;

    public bool IsWorkspaceMaterialized { get; set; }

    public string DesignGoalSummary { get; set; } = "以 70×70×5 mm 为母型基准，在满足 Q355B 强度、7.35 m 大悬臂刚度及防干涉约束下优先开展轻量化减重设计。";

    public int CurrentVersionNumber { get; set; }

    public MemberSectionSpecRecord UpperChord { get; set; } = MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆");

    public MemberSectionSpecRecord LowerChord { get; set; } = MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆");

    public MemberSectionSpecRecord LowerTruss { get; set; } = MemberSectionSpecRecord.CreateBaseline("lower_truss", "下桁架");

    public List<DesignVersionCheckpoint> VersionHistory { get; set; } = [];

    public string ActiveConversationId { get; set; } = string.Empty;

    public List<DesignConversationSummary> Conversations { get; set; } = [];

    public DesignProjectMetrics Metrics { get; set; } = new();

    [JsonIgnore]
    public string VersionTag => $"V{CurrentVersionNumber}";

    [JsonIgnore]
    public string VersionBadgeText
    {
        get
        {
            if (CurrentVersionNumber == 0 &&
                UpperChord.WidthMm == 70m && UpperChord.HeightMm == 70m && UpperChord.ThicknessMm == 5m &&
                LowerChord.WidthMm == 70m && LowerChord.HeightMm == 70m && LowerChord.ThicknessMm == 5m &&
                LowerTruss.WidthMm == 70m && LowerTruss.HeightMm == 70m && LowerTruss.ThicknessMm == 5m)
            {
                return "V0 · 母型基准 70×70×5";
            }

            return $"{VersionTag} · 上{UpperChord.SpecText} / 下{LowerChord.SpecText}";
        }
    }
}

public sealed class DesignProjectsIndexDocument
{
    public int SchemaVersion { get; set; } = 1;

    public string ActiveProjectId { get; set; } = string.Empty;

    public List<string> ProjectIds { get; set; } = [];
}
