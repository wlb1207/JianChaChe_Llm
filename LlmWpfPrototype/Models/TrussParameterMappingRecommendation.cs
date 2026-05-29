namespace LlmWpfPrototype.Models;

public sealed class TrussParameterMappingRecommendationResult
{
    public string SourcePath { get; set; } = string.Empty;

    public List<TrussMemberMappingRecommendation> Members { get; set; } = [];
}

public sealed class ConfirmedTrussMemberMapping
{
    public string MemberId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PartFilePath { get; set; } = string.Empty;

    public string ComponentName { get; set; } = string.Empty;

    public string WidthDimensionName { get; set; } = string.Empty;

    public string HeightDimensionName { get; set; } = string.Empty;

    public string ThicknessDimensionName { get; set; } = string.Empty;
}

public sealed class TrussMemberMappingRecommendation
{
    public string MemberId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<TrussPartCandidate> PartCandidates { get; set; } = [];
}

public sealed class TrussPartCandidate
{
    public string ComponentName { get; set; } = string.Empty;

    public string PartFilePath { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public DimensionCandidate? WidthCandidate { get; set; }

    public DimensionCandidate? HeightCandidate { get; set; }

    public DimensionCandidate? ThicknessCandidate { get; set; }
}

public sealed class DimensionCandidate
{
    public string DimensionName { get; set; } = string.Empty;

    public string CurrentValue { get; set; } = string.Empty;

    public string Unit { get; set; } = "mm";

    public double Confidence { get; set; }
}
