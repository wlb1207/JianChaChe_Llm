namespace LlmWpfPrototype.Models;

public sealed class SolidWorksDimensionProbeResult
{
    public string PartFilePath { get; set; } = string.Empty;

    public string RequestedDimensionName { get; set; } = string.Empty;

    public string ResolvedDimensionName { get; set; } = string.Empty;

    public string FeatureName { get; set; } = string.Empty;

    public string SketchName { get; set; } = string.Empty;

    public decimal? CurrentValue { get; set; }

    public string Unit { get; set; } = "mm";

    public bool Found { get; set; }

    public string Message { get; set; } = string.Empty;
}
