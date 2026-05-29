using System.Text.Json.Serialization;

namespace LlmWpfPrototype.Models;

public sealed class ApdlReplacement
{
    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("output_file")]
    public string OutputFile { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("variable_name")]
    public string VariableName { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("occurrence")]
    public int? Occurrence { get; set; }

    [JsonPropertyName("argument_index")]
    public int? ArgumentIndex { get; set; }

    [JsonPropertyName("regex_pattern")]
    public string RegexPattern { get; set; } = string.Empty;

    [JsonPropertyName("regex_replacement")]
    public string RegexReplacement { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
