using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmWpfPrototype.Models;

public sealed class LlmParseResult
{
    [JsonPropertyName("reply")]
    public string Reply { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("assistantText")]
    public string AssistantText { get; set; } = string.Empty;

    [JsonPropertyName("actions")]
    public List<string> Actions { get; set; } = [];

    [JsonPropertyName("commands")]
    public List<string>? Commands { get; set; }

    [JsonPropertyName("parameters")]
    public List<LlmParsedParameter> Parameters { get; set; } = [];

    [JsonPropertyName("needConfirmation")]
    public bool NeedConfirmation { get; set; }

    [JsonPropertyName("questions")]
    public List<string> Questions { get; set; } = [];
}

public sealed class LlmParsedParameter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public List<string> Target { get; set; } = [];

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}
