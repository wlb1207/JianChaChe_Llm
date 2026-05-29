using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LlmWpfPrototype.Models;

public sealed class EditableTrussMemberOptions
{
    [JsonPropertyName("EditableTrussMembers")]
    public List<EditableTrussMemberItem> EditableTrussMembers { get; set; } = [];
}

public sealed class EditableTrussMemberItem
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

public sealed class EditableTrussMemberPartMapping
{
    [JsonPropertyName("ThicknessValueMode")]
    public string ThicknessValueMode { get; set; } = string.Empty;

    [JsonPropertyName("InnerWidthValueMode")]
    public string InnerWidthValueMode { get; set; } = string.Empty;

    [JsonPropertyName("InnerHeightValueMode")]
    public string InnerHeightValueMode { get; set; } = string.Empty;

    [JsonPropertyName("InnerWidthOuterParameterKey")]
    public string InnerWidthOuterParameterKey { get; set; } = string.Empty;

    [JsonPropertyName("InnerHeightOuterParameterKey")]
    public string InnerHeightOuterParameterKey { get; set; } = string.Empty;

    [JsonPropertyName("InnerWidthThicknessParameterKey")]
    public string InnerWidthThicknessParameterKey { get; set; } = string.Empty;

    [JsonPropertyName("InnerHeightThicknessParameterKey")]
    public string InnerHeightThicknessParameterKey { get; set; } = string.Empty;

    [JsonPropertyName("SectionModelingMode")]
    public string SectionModelingMode { get; set; } = string.Empty;

    [JsonPropertyName("PartFilePath")]
    public string PartFilePath { get; set; } = string.Empty;

    [JsonPropertyName("RelativePartPath")]
    public string RelativePartPath { get; set; } = string.Empty;

    [JsonPropertyName("ComponentName")]
    public string ComponentName { get; set; } = string.Empty;

    [JsonPropertyName("WidthDimensionName")]
    public string WidthDimensionName { get; set; } = string.Empty;

    [JsonPropertyName("HeightDimensionName")]
    public string HeightDimensionName { get; set; } = string.Empty;

    [JsonPropertyName("ThicknessDimensionName")]
    public string ThicknessDimensionName { get; set; } = string.Empty;

    [JsonPropertyName("InnerWidthDimensionName")]
    public string InnerWidthDimensionName { get; set; } = string.Empty;

    [JsonPropertyName("InnerHeightDimensionName")]
    public string InnerHeightDimensionName { get; set; } = string.Empty;
}
