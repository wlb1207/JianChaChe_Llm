using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LlmWpfPrototype.Models;

public sealed class EditableTrussMembersConfig
{
    [JsonPropertyName("EditableTrussMembers")]
    public List<EditableTrussMemberConfig> EditableTrussMembers { get; set; } = [];
}

public sealed class EditableTrussMemberConfig
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Aliases")]
    public List<string> Aliases { get; set; } = [];

    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("MemberRole")]
    public string MemberRole { get; set; } = string.Empty;

    [JsonPropertyName("SectionType")]
    public string SectionType { get; set; } = string.Empty;

    [JsonPropertyName("SectionModelingMode")]
    public string SectionModelingMode { get; set; } = string.Empty;

    [JsonPropertyName("PartFilePath")]
    public string PartFilePath { get; set; } = string.Empty;

    [JsonPropertyName("RelativePartPath")]
    public string RelativePartPath { get; set; } = string.Empty;

    [JsonPropertyName("PartMappings")]
    public List<EditableTrussMemberPartMapping> PartMappings { get; set; } = [];

    [JsonPropertyName("ComponentName")]
    public string ComponentName { get; set; } = string.Empty;

    [JsonPropertyName("WidthDimensionName")]
    public string WidthDimensionName { get; set; } = string.Empty;

    [JsonPropertyName("HeightDimensionName")]
    public string HeightDimensionName { get; set; } = string.Empty;

    [JsonPropertyName("ThicknessDimensionName")]
    public string ThicknessDimensionName { get; set; } = string.Empty;

    [JsonPropertyName("Unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("MinWidth")]
    public decimal? MinWidth { get; set; }

    [JsonPropertyName("MaxWidth")]
    public decimal? MaxWidth { get; set; }

    [JsonPropertyName("MinHeight")]
    public decimal? MinHeight { get; set; }

    [JsonPropertyName("MaxHeight")]
    public decimal? MaxHeight { get; set; }

    [JsonPropertyName("MinThickness")]
    public decimal? MinThickness { get; set; }

    [JsonPropertyName("MaxThickness")]
    public decimal? MaxThickness { get; set; }

    [JsonPropertyName("Example")]
    public string Example { get; set; } = string.Empty;

    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; }
}
