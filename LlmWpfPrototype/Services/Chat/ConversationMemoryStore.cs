using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services.Chat;

public enum ConversationTurnKind
{
    Chat,
    CadAction,
    SimulationAction,
    LocalKnowledge
}

public sealed record ConversationTurnRecord(
    DateTime Timestamp,
    ConversationTurnKind Kind,
    string UserContent,
    string AssistantContent,
    KnowledgeHistoryProvenance? UserProvenance = null,
    KnowledgeHistoryProvenance? AssistantProvenance = null);

/// <summary>
/// Single source of truth for conversation turns and executed actions.
/// Exposes two strictly compliant projections:
/// 1) Main chat history (sanitized, provenance-free, compliant with KnowledgeDisclosureGuard.RejectKnowledgeOnLegacyPath)
/// 2) Local knowledge history (verified provenance only, compliant with KnowledgeDisclosureGuard.Validate)
/// </summary>
public sealed class ConversationMemoryStore
{
    public const int DefaultMaxMainMessages = 20;
    public const int DefaultMaxKnowledgeMessages = 6;
    public const int DefaultMaxRecentActions = 6;
    private const int KnowledgeSummaryCharLimit = 260;

    private static readonly Regex CitationTagRegex = new(@"\[E\d+\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VerifiedSourcePathRegex = new(@"verified/[a-zA-Z0-9_]+\.md", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LocalDrivePathRegex = new(@"[A-Za-z]:\\[^\s，。；;,]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly List<ConversationTurnRecord> _turns = [];
    private readonly List<ActionAuditEntry> _actions = [];
    private readonly int _maxMainMessages;
    private readonly int _maxKnowledgeMessages;
    private readonly int _maxRecentActions;

    public ConversationMemoryStore(
        int maxMainMessages = DefaultMaxMainMessages,
        int maxKnowledgeMessages = DefaultMaxKnowledgeMessages,
        int maxRecentActions = DefaultMaxRecentActions)
    {
        _maxMainMessages = Math.Max(2, maxMainMessages);
        _maxKnowledgeMessages = Math.Max(2, maxKnowledgeMessages);
        _maxRecentActions = Math.Max(1, maxRecentActions);
    }

    public int TurnCount => _turns.Count;

    public ActionAuditEntry? LastAction => _actions.Count == 0 ? null : _actions[^1];

    public int ApdlLauncherStartCount { get; private set; }

    public DateTime? LastApdlLauncherStartedAt { get; private set; }

    public void RecordChatTurn(string userInput, string assistantReply)
    {
        AddTurn(ConversationTurnKind.Chat, userInput, assistantReply);
    }

    public void RecordActionTurn(
        string userInput,
        string assistantReply,
        string actionName,
        string actionSummary,
        bool succeeded,
        ConversationTurnKind kind)
    {
        var now = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(actionName))
        {
            _actions.Add(new ActionAuditEntry(
                now,
                actionName.Trim(),
                SanitizeForMainHistory(actionSummary),
                succeeded));
            TrimActions();

            if (succeeded && string.Equals(actionName.Trim(), "open_apdl_launcher", StringComparison.OrdinalIgnoreCase))
            {
                ApdlLauncherStartCount++;
                LastApdlLauncherStartedAt = now;
            }
        }

        AddTurn(kind, userInput, assistantReply);
    }

    public void RecordKnowledgeTurn(
        string userInput,
        string assistantReply,
        KnowledgeHistoryProvenance? userProvenance,
        KnowledgeHistoryProvenance? answerProvenance)
    {
        AddTurn(ConversationTurnKind.LocalKnowledge, userInput, assistantReply, userProvenance, answerProvenance);
    }

    public IReadOnlyList<ConversationMessage> GetMainChatHistory()
    {
        var messages = new List<ConversationMessage>();
        foreach (var turn in _turns)
        {
            if (turn.Kind == ConversationTurnKind.LocalKnowledge)
            {
                var safeQuestion = SanitizeForMainHistory(turn.UserContent);
                var safeAnswer = SummarizeKnowledgeAnswerForMainHistory(turn.AssistantContent);
                if (!string.IsNullOrWhiteSpace(safeQuestion))
                {
                    messages.Add(new ConversationMessage
                    {
                        Role = "user",
                        Content = safeQuestion,
                        CreatedAt = turn.Timestamp
                    });
                }

                if (!string.IsNullOrWhiteSpace(safeAnswer))
                {
                    messages.Add(new ConversationMessage
                    {
                        Role = "assistant",
                        Content = safeAnswer,
                        CreatedAt = turn.Timestamp
                    });
                }

                continue;
            }

            var userText = SanitizeForMainHistory(turn.UserContent);
            var assistantText = SanitizeForMainHistory(turn.AssistantContent);
            if (!string.IsNullOrWhiteSpace(userText))
            {
                messages.Add(new ConversationMessage
                {
                    Role = "user",
                    Content = userText,
                    CreatedAt = turn.Timestamp
                });
            }

            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                messages.Add(new ConversationMessage
                {
                    Role = "assistant",
                    Content = assistantText,
                    CreatedAt = turn.Timestamp
                });
            }
        }

        if (messages.Count > _maxMainMessages)
        {
            messages.RemoveRange(0, messages.Count - _maxMainMessages);
        }

        return messages;
    }

    public IReadOnlyList<ConversationMessage> GetKnowledgeHistory()
    {
        var messages = new List<ConversationMessage>();
        foreach (var turn in _turns)
        {
            if (turn.Kind != ConversationTurnKind.LocalKnowledge ||
                turn.UserProvenance is null ||
                turn.AssistantProvenance is null)
            {
                continue;
            }

            messages.Add(new ConversationMessage
            {
                Role = "user",
                Content = turn.UserContent,
                KnowledgeProvenance = turn.UserProvenance,
                CreatedAt = turn.Timestamp
            });
            messages.Add(new ConversationMessage
            {
                Role = "assistant",
                Content = turn.AssistantContent,
                KnowledgeProvenance = turn.AssistantProvenance,
                CreatedAt = turn.Timestamp
            });
        }

        if (messages.Count > _maxKnowledgeMessages)
        {
            messages.RemoveRange(0, messages.Count - _maxKnowledgeMessages);
        }

        return messages;
    }

    public IReadOnlyList<ActionAuditEntry> GetRecentActions(int? maxCount = null)
    {
        var limit = Math.Max(1, maxCount ?? _maxRecentActions);
        return _actions.Count <= limit
            ? _actions.ToArray()
            : _actions.Skip(_actions.Count - limit).ToArray();
    }

    public void Clear()
    {
        _turns.Clear();
        _actions.Clear();
        ApdlLauncherStartCount = 0;
        LastApdlLauncherStartedAt = null;
    }

    private void AddTurn(
        ConversationTurnKind kind,
        string userInput,
        string assistantReply,
        KnowledgeHistoryProvenance? userProvenance = null,
        KnowledgeHistoryProvenance? assistantProvenance = null)
    {
        var trimmedUser = userInput?.Trim() ?? string.Empty;
        var trimmedAssistant = assistantReply?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedUser) && string.IsNullOrWhiteSpace(trimmedAssistant))
        {
            return;
        }

        _turns.Add(new ConversationTurnRecord(
            DateTime.Now,
            kind,
            trimmedUser,
            trimmedAssistant,
            userProvenance,
            assistantProvenance));

        var maxStoredTurns = Math.Max(_maxMainMessages, _maxKnowledgeMessages) * 2;
        if (_turns.Count > maxStoredTurns)
        {
            _turns.RemoveRange(0, _turns.Count - maxStoredTurns);
        }
    }

    private void TrimActions()
    {
        if (_actions.Count > _maxRecentActions * 2)
        {
            _actions.RemoveRange(0, _actions.Count - _maxRecentActions * 2);
        }
    }

    internal static string SanitizeForMainHistory(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sanitized = CitationTagRegex.Replace(text.Trim(), string.Empty);
        sanitized = VerifiedSourcePathRegex.Replace(sanitized, "本地规范资料");
        sanitized = LocalDrivePathRegex.Replace(sanitized, "[本地路径]");
        return sanitized.Trim();
    }

    private static string SummarizeKnowledgeAnswerForMainHistory(string? answer)
    {
        var sanitized = SanitizeForMainHistory(answer);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "（已在本地工程资料通道完成该问题答复）";
        }

        return sanitized.Length <= KnowledgeSummaryCharLimit
            ? sanitized
            : sanitized[..KnowledgeSummaryCharLimit] + "...";
    }
}
