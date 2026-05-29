using System.Text.Json.Serialization;

namespace LlmWpfPrototype.Models;

public sealed class SolidWorksTarget
{
    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("output_file")]
    public string OutputFile { get; set; } = string.Empty;

    [JsonPropertyName("configuration")]
    public string Configuration { get; set; } = string.Empty;

    [JsonPropertyName("dimension_name")]
    public string DimensionName { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;
}
