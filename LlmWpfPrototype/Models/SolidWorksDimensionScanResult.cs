using System.Collections.Generic;

namespace LlmWpfPrototype.Models;

public sealed class SolidWorksDimensionScanResult
{
    public string JsonOutputPath { get; set; } = string.Empty;

    public string CsvOutputPath { get; set; } = string.Empty;

    public List<SolidWorksDimensionScanItem> Items { get; set; } = [];

    public List<string> Errors { get; set; } = [];
}

public sealed class SolidWorksDimensionScanItem
{
    public string ComponentName { get; set; } = string.Empty;

    public string PartFilePath { get; set; } = string.Empty;

    public string FeatureName { get; set; } = string.Empty;

    public string SketchName { get; set; } = string.Empty;

    public string DimensionName { get; set; } = string.Empty;

    public string FullDimensionName { get; set; } = string.Empty;

    public string CurrentValue { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;
}
