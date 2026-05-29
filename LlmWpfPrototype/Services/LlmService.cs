using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class LlmService : ILlmService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const string MockMode = "Mock";
    private const string CloudMode = "Cloud";
    private const string LocalMode = "Local";
    private const string OpenAiProvider = "OpenAI";
    private const string OpenAiCompatibleProvider = "OpenAICompatible";
    private const int AssistantPreviewLimit = 1000;
    private const int RawResponsePreviewLimit = 2000;
    private const int RawErrorPreviewLimit = 2000;
    private const int RawParseFailurePreviewLimit = 2000;

    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;
    private readonly ParameterDictionaryService _parameterDictionaryService;

    public event Action<string>? DiagnosticLogEmitted;

    public LlmService(LlmOptions options, ParameterDictionaryService parameterDictionaryService)
    {
        _options = options;
        _parameterDictionaryService = parameterDictionaryService;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds))
        };
    }

    public async Task<string> ParseDesignRequirementAsync(
        string userInput,
        IReadOnlyList<ConversationMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default)
    {
        var mode = NormalizeMode(_options.Mode);
        return mode switch
        {
            MockMode => await BuildMockResponseAsync(cancellationToken),
            CloudMode => await CallRemoteApiAsync(userInput, conversationHistory, requireApiKey: true, cancellationToken),
            LocalMode => await CallRemoteApiAsync(userInput, conversationHistory, requireApiKey: false, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported LLM mode: {_options.Mode}")
        };
    }

    public async Task<LlmConnectionTestResult> TestConnectionAsync(string userInput, CancellationToken cancellationToken = default)
    {
        HttpStatusCode? statusCode = null;
        var apiKey = GetApiKey();
        var apiKeyExists = !string.IsNullOrWhiteSpace(apiKey);
        var rawResponse = string.Empty;
        var requestUrl = BuildChatCompletionsUrl(_options.ApiBaseUrl);
        var requestPath = TryGetRequestPath(requestUrl);
        var requestPayload = BuildConnectionTestPayload(userInput);
        var systemPrompt = "You are a connection test endpoint. Reply with plain text pong only.";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            EnsureProviderIsSupported();

            if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl))
            {
                throw new InvalidOperationException("ApiBaseUrl cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(_options.Model))
            {
                throw new InvalidOperationException("Model cannot be empty.");
            }

            WriteRequestDiagnostics(
                purpose: "LLM Test",
                requestUrl,
                requestPath,
                requestPayload,
                systemPrompt,
                userInput,
                stream: false,
                temperature: 0m,
                maxTokens: null,
                maxOutputTokens: null,
                apiKey);

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            if (apiKeyExists)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            request.Content = new StringContent(
                JsonSerializer.Serialize(requestPayload, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            statusCode = response.StatusCode;
            rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var metadata = TryExtractResponseMetadata(rawResponse);
            WriteResponseDiagnostics("LLM Test", response.StatusCode, metadata, rawResponse, metadata.AssistantContentPreview);

            if (!response.IsSuccessStatusCode)
            {
                WriteErrorDiagnostics(
                    purpose: "LLM Test",
                    statusCode: response.StatusCode,
                    errorResponsePreview: Truncate(rawResponse, RawErrorPreviewLimit),
                    exception: null);

                return new LlmConnectionTestResult(
                    false,
                    _options.ApiBaseUrl,
                    _options.Model,
                    apiKeyExists,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    metadata.ResponseId,
                    metadata.ResponseObject,
                    metadata.ResponseModel,
                    metadata.UsagePromptTokens,
                    metadata.UsageCompletionTokens,
                    metadata.UsageTotalTokens,
                    metadata.FinishReason,
                    metadata.AssistantContentPreview,
                    Truncate(rawResponse, RawResponsePreviewLimit),
                    Truncate(rawResponse, RawErrorPreviewLimit),
                    nameof(InvalidOperationException),
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    null);
            }

            return new LlmConnectionTestResult(
                true,
                _options.ApiBaseUrl,
                _options.Model,
                apiKeyExists,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                metadata.ResponseId,
                metadata.ResponseObject,
                metadata.ResponseModel,
                metadata.UsagePromptTokens,
                metadata.UsageCompletionTokens,
                metadata.UsageTotalTokens,
                metadata.FinishReason,
                metadata.AssistantContentPreview,
                Truncate(rawResponse, RawResponsePreviewLimit),
                null,
                null,
                null,
                null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteErrorDiagnostics(
                purpose: "LLM Test",
                statusCode,
                Truncate(rawResponse, RawErrorPreviewLimit),
                ex);

            return new LlmConnectionTestResult(
                false,
                _options.ApiBaseUrl,
                _options.Model,
                apiKeyExists,
                statusCode is null ? null : (int)statusCode.Value,
                stopwatch.ElapsedMilliseconds,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                string.Empty,
                Truncate(rawResponse, RawResponsePreviewLimit),
                Truncate(rawResponse, RawErrorPreviewLimit),
                ex.GetType().Name,
                ex.Message,
                ex.InnerException?.Message);
        }
    }

    private Task<string> BuildMockResponseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mockParameters = _parameterDictionaryService.BuildMockParameters()
            .Select(item => new LlmParsedParameter
            {
                Name = item.Name,
                DisplayName = item.DisplayName,
                Value = CreateMockJsonValue(item.Value),
                Unit = item.Unit,
                Target = [],
                Confidence = 0.95
            })
            .ToList();

        var result = new LlmParseResult
        {
            Actions = ["update_solidworks_dimensions"],
            Parameters = mockParameters,
            NeedConfirmation = false,
            Questions = []
        };

        return Task.FromResult(JsonSerializer.Serialize(result, JsonOptions));
    }

    private static JsonElement CreateMockJsonValue(string value)
    {
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return JsonDocument.Parse(value).RootElement.Clone();
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();
    }

    private async Task<string> CallRemoteApiAsync(
        string userInput,
        IReadOnlyList<ConversationMessage>? conversationHistory,
        bool requireApiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureProviderIsSupported();

            var apiKey = GetApiKey();
            if (requireApiKey && string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Cloud mode requires an API key from the configured environment variable.");
            }

            if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl))
            {
                throw new InvalidOperationException("ApiBaseUrl cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(_options.Model))
            {
                throw new InvalidOperationException("Model cannot be empty.");
            }

            var requestId = Guid.NewGuid().ToString("N");
            var preferredFormat = GetPreferredResponseFormat();
            var responseText = await SendChatCompletionAsync(
                userInput,
                conversationHistory,
                apiKey,
                preferredFormat,
                requestId,
                cancellationToken);

            return NormalizeModelResponseWithDiagnostics(responseText, userInput);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("LLM request timed out. Please check network connectivity, model latency, or TimeoutSeconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"LLM network request failed: {ex.Message}", ex);
        }
    }

    private async Task<string> SendChatCompletionAsync(
        string userInput,
        IReadOnlyList<ConversationMessage>? conversationHistory,
        string apiKey,
        ResponseFormatMode responseFormatMode,
        string requestId,
        CancellationToken cancellationToken)
    {
        var systemPrompt = BuildUnifiedConversationSystemPrompt();
        var attempts = new[]
        {
            responseFormatMode,
            ResponseFormatMode.JsonObject,
            ResponseFormatMode.None
        }.Distinct().ToArray();

        Exception? lastException = null;

        foreach (var attempt in attempts)
        {
            try
            {
                var requestUrl = BuildChatCompletionsUrl(_options.ApiBaseUrl);
                var requestPath = TryGetRequestPath(requestUrl);
                var payload = BuildPayload(userInput, conversationHistory, attempt, systemPrompt);
                WriteRequestDiagnostics(
                    purpose: "LLM Chat",
                    requestUrl,
                    requestPath,
                    payload,
                    systemPrompt,
                    userInput,
                    stream: false,
                    temperature: 0.1m,
                    maxTokens: null,
                    maxOutputTokens: null,
                    apiKey);

                using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                request.Headers.TryAddWithoutValidation("X-Client-Request-Id", requestId);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOptions),
                    Encoding.UTF8,
                    "application/json");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                var metadata = TryExtractResponseMetadata(responseText);
                WriteResponseDiagnostics("LLM Chat", response.StatusCode, metadata, responseText, metadata.AssistantContentPreview);
                if (!response.IsSuccessStatusCode)
                {
                    WriteErrorDiagnostics(
                        purpose: "LLM Chat",
                        statusCode: response.StatusCode,
                        errorResponsePreview: Truncate(responseText, RawErrorPreviewLimit),
                        exception: null);

                    var errorMessage = $"LLM 鎺ュ彛璋冪敤澶辫触锛欻TTP {(int)response.StatusCode}锛屽搷搴旓細{responseText}";
                    if (ShouldRetryWithoutStructuredFormat(response.StatusCode, responseText, attempt))
                    {
                        lastException = new InvalidOperationException(errorMessage);
                        continue;
                    }

                    throw new InvalidOperationException(errorMessage);
                }

                using var responseDocument = JsonDocument.Parse(responseText);
                var content = responseDocument.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidOperationException("LLM returned empty content.");
                }

                return content;
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                lastException = ex;
                if (attempt != ResponseFormatMode.None)
                {
                    continue;
                }

                throw;
            }
        }

        throw lastException ?? new InvalidOperationException("LLM request failed.");
    }

    private string NormalizeModelResponseWithDiagnostics(string content, string userInput)
    {
        EmitDiagnostic("[LLM Chat] ReplyTextActionInference=Disabled");
        EmitDiagnostic($"[LLM Chat] PreParseText=<redacted>, Length={content.Length}");

        try
        {
            var normalized = NormalizeModelResponse(content, userInput);
            var parseResult = JsonSerializer.Deserialize<LlmParseResult>(normalized, ParseOptions)
                ?? new LlmParseResult();
            var structuredActions = (parseResult.Actions ?? [])
                .Select(NormalizeActionName)
                .Where(IsDispatchableAction)
                .Where(action => !string.IsNullOrWhiteSpace(action))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var structuredCommands = (parseResult.Commands ?? [])
                .Select(NormalizeActionName)
                .Where(IsDispatchableAction)
                .Where(action => !string.IsNullOrWhiteSpace(action))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            EmitDiagnostic($"[LLM Chat] StructuredActions.Count={structuredActions.Count}");
            EmitDiagnostic($"[LLM Chat] StructuredCommands.Count={structuredCommands.Count}");

            var parsedActionCount = structuredActions.Count + structuredCommands
                .Except(structuredActions, StringComparer.OrdinalIgnoreCase)
                .Count();

            if (structuredActions.Count == 0 && structuredCommands.Count == 0 && parsedActionCount != 0)
            {
                EmitDiagnostic($"[LLM Chat] ActionParseMismatch=True StructuredActions=0 StructuredCommands=0 ParsedActionCount={parsedActionCount}");
                EmitDiagnostic("[LLM Chat] ParsedActionsCleared Reason=no_structured_actions");
                parsedActionCount = 0;
            }

            EmitDiagnostic($"[LLM Chat] ParsedActionCount={parsedActionCount}");
            return normalized;
        }
        catch (LlmResponseParseException ex)
        {
            EmitDiagnostic("[LLM Chat] ParsedActionCount=0");
            EmitDiagnostic($"[LLM Chat] RawResponsePreview=<redacted>, Length={ex.RawResponsePreview.Length}");
            throw;
        }
    }

    private object BuildPayload(
        string userInput,
        IReadOnlyList<ConversationMessage>? conversationHistory,
        ResponseFormatMode responseFormatMode,
        string? systemPrompt = null)
    {
        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = systemPrompt ?? BuildUnifiedConversationSystemPrompt()
            }
        };

        if (conversationHistory is not null)
        {
            foreach (var item in conversationHistory)
            {
                if (item is null ||
                    string.IsNullOrWhiteSpace(item.Role) ||
                    string.IsNullOrWhiteSpace(item.Content))
                {
                    continue;
                }

                var normalizedRole = item.Role.Trim().ToLowerInvariant();
                if (normalizedRole is not ("user" or "assistant"))
                {
                    continue;
                }

                messages.Add(new
                {
                    role = normalizedRole,
                    content = item.Content.Trim()
                });
            }
        }

        messages.Add(new
        {
            role = "user",
            content = userInput
        });

        var basePayload = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = messages.ToArray(),
            ["temperature"] = 0.1
        };

        switch (responseFormatMode)
        {
            case ResponseFormatMode.StrictSchema when CanUseStrictSchema():
                basePayload["response_format"] = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "bridge_car_design_parse",
                        strict = true,
                        schema = CreateJsonSchema()
                    }
                };
                break;
            case ResponseFormatMode.JsonObject:
                basePayload["response_format"] = new
                {
                    type = "json_object"
                };
                break;
        }

        return basePayload;
    }

    private object BuildConnectionTestPayload(string userInput)
    {
        return new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are a connection test endpoint. Reply with plain text pong only."
                },
                new
                {
                    role = "user",
                    content = userInput
                }
            },
            ["temperature"] = 0
        };
    }

    private static object CreateJsonSchema()
    {
        return new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                actions = new
                {
                    type = "array",
                    items = new { type = "string" }
                },
                parameters = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            name = new { type = "string" },
                            displayName = new { type = "string" },
                            value = new
                            {
                                anyOf = new object[]
                                {
                                    new { type = "number" },
                                    new { type = "string" },
                                    new { type = "null" }
                                }
                            },
                            unit = new { type = "string" },
                            target = new
                            {
                                type = "array",
                                items = new { type = "string" }
                            },
                            confidence = new { type = "number" }
                        },
                        required = new[] { "name", "displayName", "value", "unit", "target", "confidence" }
                    }
                },
                needConfirmation = new { type = "boolean" },
                questions = new
                {
                    type = "array",
                    items = new { type = "string" }
                }
            },
            required = new[] { "actions", "parameters", "needConfirmation", "questions" }
        };
    }

    private ResponseFormatMode GetPreferredResponseFormat()
    {
        if (CanUseStrictSchema())
        {
            return ResponseFormatMode.StrictSchema;
        }

        return ResponseFormatMode.JsonObject;
    }

    private void EnsureProviderIsSupported()
    {
        if (!string.Equals(_options.Provider, OpenAiProvider, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_options.Provider, OpenAiCompatibleProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"褰撳墠浠呮敮鎸?{OpenAiProvider} 鎴?{OpenAiCompatibleProvider} Provider锛屾敹鍒帮細{_options.Provider}");
        }
    }

    private bool CanUseStrictSchema()
    {
        if (!string.Equals(_options.Provider, OpenAiProvider, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsOfficialOpenAiHost(_options.ApiBaseUrl);
    }

    private string GetApiKey()
    {
        if (!string.Equals(_options.Mode, CloudMode, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var envName = string.IsNullOrWhiteSpace(_options.ApiKeyEnvName) ? "OPENAI_API_KEY" : _options.ApiKeyEnvName;
        return Environment.GetEnvironmentVariable(envName) ?? string.Empty;
    }

    private static string NormalizeMode(string mode)
    {
        if (string.Equals(mode, MockMode, StringComparison.OrdinalIgnoreCase))
        {
            return MockMode;
        }

        if (string.Equals(mode, CloudMode, StringComparison.OrdinalIgnoreCase))
        {
            return CloudMode;
        }

        if (string.Equals(mode, LocalMode, StringComparison.OrdinalIgnoreCase))
        {
            return LocalMode;
        }

        return mode;
    }

    private static string BuildChatCompletionsUrl(string apiBaseUrl)
    {
        var trimmed = apiBaseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed}/chat/completions";
    }

    private void WriteRequestDiagnostics(
        string purpose,
        string requestUrl,
        string requestPath,
        object payload,
        string systemPrompt,
        string userPrompt,
        bool stream,
        decimal temperature,
        int? maxTokens,
        int? maxOutputTokens,
        string apiKey)
    {
        EmitDiagnostic($"[{purpose}] Provider={_options.Provider}");
        EmitDiagnostic($"[{purpose}] ApiBaseUrl=<redacted>");
        EmitDiagnostic($"[{purpose}] RequestUrl={requestPath}");
        EmitDiagnostic($"[{purpose}] RequestModel={_options.Model}");
        EmitDiagnostic($"[{purpose}] Stream={stream}");
        EmitDiagnostic($"[{purpose}] SystemPromptChars={systemPrompt.Length}");
        EmitDiagnostic($"[{purpose}] UserPromptChars={userPrompt.Length}");
        EmitDiagnostic($"[{purpose}] Messages.Count={TryGetMessageCount(payload)}");
        EmitDiagnostic($"[{purpose}] Temperature={temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        EmitDiagnostic($"[{purpose}] MaxTokens={(maxTokens?.ToString() ?? "n/a")}");
        EmitDiagnostic($"[{purpose}] MaxOutputTokens={(maxOutputTokens?.ToString() ?? "n/a")}");
        EmitDiagnostic($"[{purpose}] TimeoutSeconds={Math.Max(1, _options.TimeoutSeconds)}");
        EmitDiagnostic($"[{purpose}] Authorization=<redacted>");
        EmitDiagnostic($"[{purpose}] FullRequestUrl=<redacted>");
    }

    private void WriteResponseDiagnostics(
        string purpose,
        HttpStatusCode statusCode,
        LlmResponseMetadata metadata,
        string rawResponse,
        string assistantPreview)
    {
        EmitDiagnostic($"[{purpose}] HTTP StatusCode={(int)statusCode}");
        EmitDiagnostic($"[{purpose}] ResponseId={metadata.ResponseId ?? "n/a"}");
        EmitDiagnostic($"[{purpose}] ResponseObject={metadata.ResponseObject ?? "n/a"}");
        EmitDiagnostic($"[{purpose}] ResponseModel={metadata.ResponseModel ?? "n/a"}");
        EmitDiagnostic($"[{purpose}] UsagePromptTokens={(metadata.UsagePromptTokens?.ToString() ?? "n/a")}");
        EmitDiagnostic($"[{purpose}] UsageCompletionTokens={(metadata.UsageCompletionTokens?.ToString() ?? "n/a")}");
        EmitDiagnostic($"[{purpose}] UsageTotalTokens={(metadata.UsageTotalTokens?.ToString() ?? "n/a")}");
        EmitDiagnostic($"[{purpose}] FinishReason={metadata.FinishReason ?? "n/a"}");
        EmitDiagnostic($"[{purpose}] RawAssistantContentPreview=<redacted>, Length={assistantPreview.Length}");
        EmitDiagnostic($"[{purpose}] RawResponsePreview=<redacted>, Length={rawResponse.Length}");
    }

    private void WriteErrorDiagnostics(
        string purpose,
        HttpStatusCode? statusCode,
        string errorResponsePreview,
        Exception? exception)
    {
        EmitDiagnostic($"[{purpose}] ErrorStatusCode={(statusCode is null ? "n/a" : ((int)statusCode.Value).ToString())}");
        EmitDiagnostic($"[{purpose}] ErrorResponsePreview=<redacted>, Length={errorResponsePreview.Length}");
        EmitDiagnostic($"[{purpose}] ExceptionType={exception?.GetType().FullName ?? "n/a"}");
        EmitDiagnostic($"[{purpose}] ExceptionMessage={exception?.Message ?? "n/a"}");
        EmitDiagnostic($"[{purpose}] InnerExceptionMessage={exception?.InnerException?.Message ?? "n/a"}");
    }

    private void EmitDiagnostic(string message)
    {
        DiagnosticLogEmitted?.Invoke(message);
    }

    private static string TryGetRequestPath(string requestUrl)
    {
        return Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : requestUrl;
    }

    private static int TryGetMessageCount(object payload)
    {
        if (payload is not Dictionary<string, object?> dictionary ||
            !dictionary.TryGetValue("messages", out var messages) ||
            messages is not Array array)
        {
            return 0;
        }

        return array.Length;
    }

    private static string MaskAuthorization(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "not-set";
        }

        return "Configured";
    }

    private static bool IsOfficialOpenAiHost(string apiBaseUrl)
    {
        return Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRetryWithoutStructuredFormat(HttpStatusCode statusCode, string responseText, ResponseFormatMode responseFormatMode)
    {
        if (responseFormatMode == ResponseFormatMode.None)
        {
            return false;
        }

        var status = (int)statusCode;
        if (status >= 500 || status == 429)
        {
            return true;
        }

        return responseText.Contains("do_request_failed", StringComparison.OrdinalIgnoreCase)
            || responseText.Contains("upstream error", StringComparison.OrdinalIgnoreCase)
            || responseText.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || responseText.Contains("json_schema", StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateForPreview(string value)
    {
        return Truncate(value, 500);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static LlmResponseMetadata TryExtractResponseMetadata(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return LlmResponseMetadata.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;
            var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;
            var choice = TryGetFirstChoice(root);

            return new LlmResponseMetadata(
                TryGetString(root, "id"),
                TryGetString(root, "object"),
                TryGetString(root, "model"),
                TryGetInt32(usage, "prompt_tokens"),
                TryGetInt32(usage, "completion_tokens"),
                TryGetInt32(usage, "total_tokens"),
                choice.ValueKind == JsonValueKind.Object ? TryGetString(choice, "finish_reason") : null,
                Truncate(TryExtractAssistantContent(root), AssistantPreviewLimit));
        }
        catch
        {
            return LlmResponseMetadata.Empty;
        }
    }

    private static JsonElement TryGetFirstChoice(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            return choices[0];
        }

        return default;
    }

    private static string TryExtractAssistantContent(JsonElement root)
    {
        var choice = TryGetFirstChoice(root);
        if (choice.ValueKind == JsonValueKind.Object &&
            choice.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            return content.GetRawText();
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("output_text", out var outputText) &&
            outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private string NormalizeModelResponse(string content, string userInput)
    {
        try
        {
            var normalizedContent = ExtractJsonPayload(content);
            using var strictJsonCheck = JsonDocument.Parse(normalizedContent);
            var normalizedJson = NormalizeRootJson(strictJsonCheck.RootElement);
            var parseResult = JsonSerializer.Deserialize<LlmParseResult>(normalizedJson, ParseOptions)
                ?? new LlmParseResult();
            NormalizeParseResultShape(parseResult, userInput);
            return JsonSerializer.Serialize(parseResult, JsonOptions);
        }
        catch (Exception ex) when (ex is not LlmResponseParseException)
        {
            throw new LlmResponseParseException(
                "Failed to parse LLM response.",
                Truncate(content, RawParseFailurePreviewLimit),
                ex);
        }
    }

    private static string ExtractJsonPayload(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("LLM returned empty content.");
        }

        var trimmed = content.Trim().Trim('\uFEFF');
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("LLM returned empty content.");
        }

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = StripMarkdownCodeFence(trimmed);
        }

        if (LooksLikeHtml(trimmed))
        {
            throw new InvalidOperationException(
                $"LLM 杩斿洖浜?HTML 鑰屼笉鏄?JSON銆傝妫€鏌?API 鍦板潃銆侀壌鏉冧俊鎭垨涓婃父鏈嶅姟鐘舵€併€傜墖娈碉細{BuildSnippet(trimmed)}");
        }

        if (TryParseJsonCandidate(trimmed, out var strictJson))
        {
            return strictJson;
        }

        var fencedContent = TryExtractFirstCodeFenceContent(content);
        if (!string.IsNullOrWhiteSpace(fencedContent) && TryParseJsonCandidate(fencedContent, out var fencedJson))
        {
            return fencedJson;
        }

        if (TryExtractBestJsonPayload(trimmed, out var extractedNormalizedJson))
        {
            return extractedNormalizedJson;
        }

        if (!string.IsNullOrWhiteSpace(fencedContent) &&
            TryExtractBestJsonPayload(fencedContent, out var extractedFenceNormalizedJson))
        {
            return extractedFenceNormalizedJson;
        }

        throw new InvalidOperationException(
            $"LLM 杩斿洖鍐呭鏃犳硶瑙ｆ瀽涓?JSON銆傜墖娈碉細{BuildSnippet(trimmed)}");
    }

    private static string StripMarkdownCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return trimmed.Trim('`').Trim();
        }

        var withoutHeader = trimmed[(firstLineEnd + 1)..];
        var closingFenceIndex = withoutHeader.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceIndex >= 0)
        {
            withoutHeader = withoutHeader[..closingFenceIndex];
        }

        return withoutHeader.Trim();
    }

    private static bool LooksLikeHtml(string content)
    {
        return content.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith("<body", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindFirstJsonStartIndex(string content)
    {
        var objectIndex = content.IndexOf('{');
        var arrayIndex = content.IndexOf('[');

        if (objectIndex < 0)
        {
            return arrayIndex;
        }

        if (arrayIndex < 0)
        {
            return objectIndex;
        }

        return Math.Min(objectIndex, arrayIndex);
    }

    private static bool TryParseJsonCandidate(string candidate, out string normalizedJson)
    {
        normalizedJson = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(candidate.Trim());
            normalizedJson = NormalizeRootJson(document.RootElement);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeRootJson(JsonElement rootElement)
    {
        if (rootElement.ValueKind == JsonValueKind.Object)
        {
            return rootElement.GetRawText();
        }

        if (rootElement.ValueKind == JsonValueKind.Array)
        {
            var bestItem = rootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new
                {
                    Json = item.GetRawText(),
                    Score = ScoreSchemaLikelihood(item)
                })
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();

            if (bestItem is not null)
            {
                return bestItem.Json;
            }
        }

        throw new InvalidOperationException("The JSON root returned by LLM is not a recognizable object or array.");
    }

    private static bool TryExtractBestJsonPayload(string content, out string normalizedJson)
    {
        normalizedJson = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        string? bestJson = null;
        var bestScore = int.MinValue;

        for (var startIndex = 0; startIndex < content.Length; startIndex++)
        {
            if (content[startIndex] is not ('{' or '['))
            {
                continue;
            }

            if (!TryReadCompleteJsonSegment(content, startIndex, out var endIndex))
            {
                continue;
            }

            var candidate = content[startIndex..(endIndex + 1)].Trim();
            if (!TryParseJsonCandidate(candidate, out var candidateJson))
            {
                continue;
            }

            using var candidateDocument = JsonDocument.Parse(candidateJson);
            var score = ScoreSchemaLikelihood(candidateDocument.RootElement);
            if (score > bestScore)
            {
                bestScore = score;
                bestJson = candidateJson;
            }
        }

        if (string.IsNullOrWhiteSpace(bestJson))
        {
            return false;
        }

        normalizedJson = bestJson;
        return true;
    }

    private void NormalizeParseResultShape(LlmParseResult parseResult, string userInput)
    {
        if (parseResult.Commands is { Count: > 0 })
        {
            parseResult.Actions = parseResult.Commands
                .Where(command => !string.IsNullOrWhiteSpace(command))
                .ToList();
        }

        var rawParameterCount = parseResult.Parameters.Count;
        EmitDiagnostic($"[LLM Chat] RawParameterCount={rawParameterCount}");
        parseResult.Parameters = FilterValidParameters(parseResult.Parameters);
        EmitDiagnostic($"[LLM Chat] ValidParameterCount={parseResult.Parameters.Count}");
        EmitDiagnostic($"[LLM Chat] IgnoredInvalidParameterCount={rawParameterCount - parseResult.Parameters.Count}");

        parseResult.Actions = BuildNormalizedActions(userInput, parseResult);
        parseResult.Commands = parseResult.Actions.ToList();

        parseResult.Reply = ResolveAssistantReply(parseResult);
        if (string.IsNullOrWhiteSpace(parseResult.Message))
        {
            parseResult.Message = parseResult.Reply;
        }

        if (string.IsNullOrWhiteSpace(parseResult.AssistantText))
        {
            parseResult.AssistantText = parseResult.Reply;
        }
    }

    private List<LlmParsedParameter> FilterValidParameters(IEnumerable<LlmParsedParameter> parameters)
    {
        var validParameters = new List<LlmParsedParameter>();
        var index = 0;

        foreach (var parameter in parameters)
        {
            var reasons = GetInvalidParameterReasons(parameter);
            if (reasons.Count == 0)
            {
                validParameters.Add(parameter);
            }
            else
            {
                foreach (var reason in reasons)
                {
                    EmitDiagnostic($"[LLM Chat] IgnoredParameter[{index}] {reason}");
                }
            }

            index++;
        }

        return validParameters;
    }

    private List<string> GetInvalidParameterReasons(LlmParsedParameter parameter)
    {
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(parameter.Name))
        {
            reasons.Add("ignored parameter because name is empty");
        }

        if (string.IsNullOrWhiteSpace(parameter.Name) && string.IsNullOrWhiteSpace(parameter.DisplayName))
        {
            reasons.Add("ignored parameter because name and displayName are both empty");
        }

        if (!parameter.Value.HasValue ||
            parameter.Value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            reasons.Add("ignored parameter because value is null");
        }

        if (parameter.Confidence <= 0)
        {
            reasons.Add("ignored parameter because confidence <= 0");
        }

        if ((parameter.Target is null || parameter.Target.Count == 0) && !CanResolveParameterDefinition(parameter))
        {
            reasons.Add("ignored parameter because target is empty and parameter dictionary mapping is unavailable");
        }

        return reasons;
    }

    private bool CanResolveParameterDefinition(LlmParsedParameter parameter)
    {
        try
        {
            var definitions = _parameterDictionaryService.LoadDefinitions();
            return definitions.Any(definition =>
                MatchesDefinitionCandidate(definition.Name, parameter.Name) ||
                MatchesDefinitionCandidate(definition.DisplayName, parameter.Name) ||
                definition.Aliases.Any(alias => MatchesDefinitionCandidate(alias, parameter.Name)) ||
                MatchesDefinitionCandidate(definition.Name, parameter.DisplayName) ||
                MatchesDefinitionCandidate(definition.DisplayName, parameter.DisplayName) ||
                definition.Aliases.Any(alias => MatchesDefinitionCandidate(alias, parameter.DisplayName)));
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesDefinitionCandidate(string definitionValue, string candidate)
    {
        return !string.IsNullOrWhiteSpace(definitionValue) &&
               !string.IsNullOrWhiteSpace(candidate) &&
               string.Equals(definitionValue.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAssistantReply(LlmParseResult parseResult)
    {
        if (!string.IsNullOrWhiteSpace(parseResult.Reply))
        {
            return parseResult.Reply.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parseResult.Message))
        {
            return parseResult.Message.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parseResult.AssistantText))
        {
            return parseResult.AssistantText.Trim();
        }

        if (parseResult.Questions.Count > 0)
        {
            return string.Join(Environment.NewLine, parseResult.Questions.Where(item => !string.IsNullOrWhiteSpace(item)));
        }

        return string.Empty;
    }

    private static string? TryExtractFirstCodeFenceContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var firstFenceStart = content.IndexOf("```", StringComparison.Ordinal);
        if (firstFenceStart < 0)
        {
            return null;
        }

        var headerEnd = content.IndexOf('\n', firstFenceStart);
        if (headerEnd < 0)
        {
            return null;
        }

        var fenceBodyStart = headerEnd + 1;
        var closingFenceStart = content.IndexOf("```", fenceBodyStart, StringComparison.Ordinal);
        if (closingFenceStart < 0)
        {
            return content[fenceBodyStart..].Trim();
        }

        return content[fenceBodyStart..closingFenceStart].Trim();
    }

    private static bool TryExtractFirstCompleteJson(string content, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        for (var startIndex = 0; startIndex < content.Length; startIndex++)
        {
            if (content[startIndex] is not ('{' or '['))
            {
                continue;
            }

            if (TryReadCompleteJsonSegment(content, startIndex, out var endIndex))
            {
                json = content[startIndex..(endIndex + 1)].Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryReadCompleteJsonSegment(string content, int startIndex, out int endIndex)
    {
        endIndex = -1;
        var expectedClosings = new Stack<char>();
        expectedClosings.Push(content[startIndex] == '{' ? '}' : ']');

        var inString = false;
        var escapeNext = false;

        for (var index = startIndex + 1; index < content.Length; index++)
        {
            var current = content[index];

            if (inString)
            {
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (current == '\\')
                {
                    escapeNext = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '{')
            {
                expectedClosings.Push('}');
                continue;
            }

            if (current == '[')
            {
                expectedClosings.Push(']');
                continue;
            }

            if (current is '}' or ']')
            {
                if (expectedClosings.Count == 0 || current != expectedClosings.Peek())
                {
                    return false;
                }

                expectedClosings.Pop();
                if (expectedClosings.Count == 0)
                {
                    endIndex = index;
                    return true;
                }
            }
        }

        return false;
    }

    private static int ScoreSchemaLikelihood(JsonElement rootElement)
    {
        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var score = 0;

        if (rootElement.TryGetProperty("actions", out var actions))
        {
            score += actions.ValueKind == JsonValueKind.Array ? 30 : 8;
            if (actions.ValueKind == JsonValueKind.Array)
            {
                score += actions.EnumerateArray().Count(item => item.ValueKind == JsonValueKind.String) * 2;
            }
        }

        if (rootElement.TryGetProperty("commands", out var commands))
        {
            score += commands.ValueKind == JsonValueKind.Array ? 30 : 8;
            if (commands.ValueKind == JsonValueKind.Array)
            {
                score += commands.EnumerateArray().Count(item => item.ValueKind == JsonValueKind.String) * 2;
            }
        }

        if (rootElement.TryGetProperty("parameters", out var parameters))
        {
            score += parameters.ValueKind == JsonValueKind.Array ? 35 : 10;
            if (parameters.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in parameters.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
                {
                    score += 4;

                    if (parameter.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        score += 4;
                    }

                    if (parameter.TryGetProperty("displayName", out var displayName) && displayName.ValueKind == JsonValueKind.String)
                    {
                        score += 4;
                    }

                    if (parameter.TryGetProperty("unit", out var unit) && unit.ValueKind == JsonValueKind.String)
                    {
                        score += 3;
                    }

                    if (parameter.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Array)
                    {
                        score += 4;
                    }

                    if (parameter.TryGetProperty("confidence", out var confidence) &&
                        (confidence.ValueKind == JsonValueKind.Number || confidence.ValueKind == JsonValueKind.String))
                    {
                        score += 3;
                    }
                }
            }
        }

        if (rootElement.TryGetProperty("needConfirmation", out var needConfirmation))
        {
            score += needConfirmation.ValueKind == JsonValueKind.True || needConfirmation.ValueKind == JsonValueKind.False ? 20 : 6;
        }

        if (rootElement.TryGetProperty("questions", out var questions))
        {
            score += questions.ValueKind == JsonValueKind.Array ? 20 : 6;
            if (questions.ValueKind == JsonValueKind.Array)
            {
                score += questions.EnumerateArray().Count(item => item.ValueKind == JsonValueKind.String) * 2;
            }
        }

        if (rootElement.TryGetProperty("reply", out var reply) && reply.ValueKind == JsonValueKind.String)
        {
            score += 20;
        }

        if (rootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            score += 15;
        }

        if (rootElement.TryGetProperty("assistantText", out var assistantText) && assistantText.ValueKind == JsonValueKind.String)
        {
            score += 15;
        }

        try
        {
            var parseResult = JsonSerializer.Deserialize<LlmParseResult>(rootElement.GetRawText(), ParseOptions);
            if (parseResult is not null)
            {
                score += 10;
            }
        }
        catch
        {
        }

        return score;
    }

    private static string BuildSnippet(string content)
    {
        const int maxLength = 200;
        return content.Length <= maxLength
            ? content
            : content[..maxLength] + "...";
    }

    private static string BuildUnifiedConversationSystemPrompt()
    {
        var schema = new
        {
            reply = string.Empty,
            commands = Array.Empty<string>(),
            actions = Array.Empty<string>(),
            parameters = new[]
            {
                new
                {
                    name = string.Empty,
                    displayName = string.Empty,
                    value = (object?)null,
                    unit = string.Empty,
                    target = Array.Empty<string>(),
                    confidence = 0.0
                }
            },
            needConfirmation = false,
            questions = Array.Empty<string>()
        };

        var schemaJson = JsonSerializer.Serialize(schema, CompactJsonOptions);
        return $"""
You are a bridge inspection vehicle model design assistant.
You must return pure JSON only. Do not output Markdown, code fences, or explanatory text outside JSON.
Do not directly operate SolidWorks. Do not generate or guess model file paths, assembly paths, part paths, feature names, sketch names, or dimension names.
The program will execute supported commands through a fixed dispatcher path.

Use this schema:
{schemaJson}

Rules:
1. `reply` is the user-visible assistant reply.
2. `commands` is the command list for execution.
3. `actions` should mirror `commands` for compatibility.
4. `parameters` contains structured parameter extraction.
5. `needConfirmation=true` when information is insufficient.
6. `questions` contains follow-up questions when needed.
7. If the user is chatting, greeting, asking for help, or asking what the system can do, return `commands=[]`, `actions=[]`, and `parameters=[]`.
8. If the user asks to open or view the model, use `open_working_model`.
9. If the user asks to reset, clear, undo, or restore modifications, use `reset_model_workspace`.
10. If the user asks to modify model dimensions or truss member parameters, use `update_solidworks_dimensions`.
11. If the user asks about editable parameters, use `query_editable_parameters`.
12. Do not ask the user to choose component names, part file paths, sketch names, feature names, or dimension names.
13. Do not recommend manual parameter mapping from scan results in user-visible replies.
14. Do not tell the user to scan dimensions, view model structure, choose candidate parts, or manually configure internal parameter mappings.
15. When describing capabilities, say you can open the current model, query editable parameters, and modify configured dimensions.
16. If multiple actions are needed, prefer this order: `reset_model_workspace`, `update_solidworks_dimensions`, `open_working_model`.
17. If the user gives incomplete modification information, ask follow-up questions in `reply` and return empty `commands`.
18. Do not return placeholder parameters, empty parameter objects, or parameters whose `name` is empty.
19. Do not return parameters whose `value` is null.
20. Only return `parameters` when the user explicitly asks to open the model, query parameters, or modify configured dimensions.
21. You are a section-size modification assistant for the SolidWorks reaction-frame model. Focus on truss member section dimensions.
22. When the user asks what can be modified, which dimensions can be changed, editable parameters, or how to modify section size, do not output long internal parameter lists.
23. For those general capability questions, do not show current values, mapping status, confidence, target details, or internal dimension names.
24. For those general capability questions, reply concisely that the current supported modification is configured truss member section dimensions. If possible, mention configured groups such as truss upper chord section size and truss lower chord section size, with examples:
    - 把桁架上弦杆截面改成 80x80x6
    - 把桁架下弦杆截面改成 80x80x6
    - 把上弦杆截面宽度改成 80
    - 把下弦杆截面高度改成 100
    - 把上弦杆壁厚改成 6
25. Do not invent unrelated examples such as main beam length or platform width.
26. Only show detailed parameter lists when the user explicitly asks to list all editable parameters, show detailed parameters, show mapping status, or view internal/config parameters.
27. For greetings, help, or capability questions, do not say only the upper chord is supported. Explain more generally that you can open the current model, query configurable member section parameters, and modify the SolidWorks model from target dimensions.
28. A good greeting/help reply is: 你好，我可以帮你打开当前模型、查询可修改的杆件截面参数，并根据你输入的目标尺寸修改 SolidWorks 模型。
29. For greeting/help examples, prefer:
    - 查询可编辑参数
    - 把桁架上弦杆截面改成 80x80x6
    - 把桁架下弦杆截面改成 80x80x6
""";
    }

    private static string BuildSystemPrompt()
    {
        return BuildUnifiedConversationSystemPrompt();
    }

    private static List<string> BuildNormalizedActions(string userInput, LlmParseResult parseResult)
    {
        var actions = parseResult.Actions
            .Select(NormalizeActionName)
            .Where(IsDispatchableAction)
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return actions;
    }

    private static bool IsModelStatusQuestion(string normalizedInput)
    {
        var hasModelKeyword = normalizedInput.Contains("模型", StringComparison.Ordinal) ||
                              normalizedInput.Contains("装配体", StringComparison.Ordinal) ||
                              normalizedInput.Contains("solidworks", StringComparison.OrdinalIgnoreCase);
        if (!hasModelKeyword)
        {
            return false;
        }

        return (normalizedInput.Contains("打开", StringComparison.Ordinal) && normalizedInput.Contains("吗", StringComparison.Ordinal)) ||
               normalizedInput.Contains("有没有", StringComparison.Ordinal) ||
               normalizedInput.Contains("是否", StringComparison.Ordinal) ||
               normalizedInput.Contains("开着吗", StringComparison.Ordinal) ||
               normalizedInput.EndsWith("了吗", StringComparison.Ordinal) ||
               normalizedInput.EndsWith("吗", StringComparison.Ordinal) ||
               normalizedInput.EndsWith("吗？", StringComparison.Ordinal) ||
               normalizedInput.EndsWith("么", StringComparison.Ordinal) ||
               normalizedInput.EndsWith("么？", StringComparison.Ordinal);
    }

    private static string NormalizeActionName(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return string.Empty;
        }

        return action.Trim() switch
        {
            var value when value.Equals("greeting", StringComparison.OrdinalIgnoreCase) => "greeting",
            var value when value.Equals("welcome", StringComparison.OrdinalIgnoreCase) => "greeting",
            var value when value.Equals("start_conversation", StringComparison.OrdinalIgnoreCase) => "greeting",
            var value when value.Equals("capability_intro", StringComparison.OrdinalIgnoreCase) => "capability_intro",
            var value when value.Equals("greeting_capability_intro", StringComparison.OrdinalIgnoreCase) => "capability_intro",
            var value when value.Equals("welcome_capability_intro", StringComparison.OrdinalIgnoreCase) => "capability_intro",
            var value when value.Equals("next_step_guidance", StringComparison.OrdinalIgnoreCase) => "next_step_guidance",
            var value when value.Equals("query_editable_parameters", StringComparison.OrdinalIgnoreCase) => "query_editable_parameters",
            var value when value.Equals("reset_model_workspace", StringComparison.OrdinalIgnoreCase) => "reset_model_workspace",
            var value when value.Equals("scan_solidworks_dimensions", StringComparison.OrdinalIgnoreCase) => "scan_solidworks_dimensions",
            var value when value.Equals("recommend_truss_parameter_mapping", StringComparison.OrdinalIgnoreCase) => "recommend_truss_parameter_mapping",
            var value when value.Equals("confirm_truss_parameter_mapping", StringComparison.OrdinalIgnoreCase) => "confirm_truss_parameter_mapping",
            var value when value.Equals("open_working_model", StringComparison.OrdinalIgnoreCase) => "open_working_model",
            var value when value.Equals("update_solidworks_dimensions", StringComparison.OrdinalIgnoreCase) => "update_solidworks_dimensions",
            var value when value.Equals("open_model", StringComparison.OrdinalIgnoreCase) => "open_working_model",
            var value when value.Equals("open_current_model", StringComparison.OrdinalIgnoreCase) => "open_working_model",
            var value when value.Equals("open_solidworks_model", StringComparison.OrdinalIgnoreCase) => "open_working_model",
            var value when value.Equals("open_solidworks_assembly", StringComparison.OrdinalIgnoreCase) => "open_working_model",
            var value when value.Equals("open_bridge_inspection_vehicle_model", StringComparison.OrdinalIgnoreCase) => "open_working_model",
            var value when value.Equals("open_initial_model", StringComparison.OrdinalIgnoreCase) => "open_working_model",
            var value when value.Equals("create_initial_model", StringComparison.OrdinalIgnoreCase) => "open_working_model",
            _ => action.Trim()
        };
    }

    private static bool IsDispatchableAction(string action)
    {
        return action.Equals("query_editable_parameters", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("reset_model_workspace", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("scan_solidworks_dimensions", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("recommend_truss_parameter_mapping", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("confirm_truss_parameter_mapping", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("open_working_model", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("update_solidworks_dimensions", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeInput(string input)
    {
        return string.IsNullOrWhiteSpace(input)
            ? string.Empty
            : input.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private static bool ContainsAny(string input, params string[] candidates)
    {
        return candidates.Any(candidate =>
            input.Contains(candidate.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant(), StringComparison.Ordinal));
    }

    private enum ResponseFormatMode
    {
        None,
        JsonObject,
        StrictSchema
    }

    private sealed record LlmResponseMetadata(
        string? ResponseId,
        string? ResponseObject,
        string? ResponseModel,
        int? UsagePromptTokens,
        int? UsageCompletionTokens,
        int? UsageTotalTokens,
        string? FinishReason,
        string AssistantContentPreview)
    {
        public static LlmResponseMetadata Empty { get; } = new(null, null, null, null, null, null, null, string.Empty);
    }
}

