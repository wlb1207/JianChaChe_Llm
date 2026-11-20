using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed record LlmConnectionTestResult(
    bool Succeeded,
    string ApiBaseUrl,
    string ModelName,
    bool ApiKeyExists,
    int? StatusCode,
    long? ElapsedMilliseconds,
    string? ResponseId,
    string? ResponseObject,
    string? ResponseModel,
    int? UsagePromptTokens,
    int? UsageCompletionTokens,
    int? UsageTotalTokens,
    string? FinishReason,
    string RawAssistantContentPreview,
    string RawResponsePreview,
    string? ErrorResponsePreview,
    string? ExceptionType,
    string? ExceptionMessage,
    string? InnerExceptionMessage);

public sealed class LlmResponseParseException : InvalidOperationException
{
    public LlmResponseParseException(string message, string rawResponsePreview, Exception? innerException = null)
        : base(message, innerException)
    {
        RawResponsePreview = rawResponsePreview;
    }

    public string RawResponsePreview { get; }
}

public sealed record LlmChatRequestOptions(
    bool ReplyOnlyMode = false,
    string? IntentLabel = null);

public interface ILlmService
{
    event Action<string>? DiagnosticLogEmitted;

    Task<string> ParseDesignRequirementAsync(
        string userInput,
        IReadOnlyList<ConversationMessage>? conversationHistory = null,
        LlmChatRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default);

    Task<LlmConnectionTestResult> TestConnectionAsync(string userInput, CancellationToken cancellationToken = default);
}
