using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LlmWpfPrototype.Models;

public sealed class ParameterDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = [];

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("default_value")]
    public string DefaultValue { get; set; } = string.Empty;

    [JsonPropertyName("min_value")]
    public decimal? MinValue { get; set; }

    [JsonPropertyName("max_value")]
    public decimal? MaxValue { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("solidworks_targets")]
    public List<SolidWorksTarget> SolidWorksTargets { get; set; } = [];

    [JsonPropertyName("apdl_replacements")]
    public List<ApdlReplacement> ApdlReplacements { get; set; } = [];
}
