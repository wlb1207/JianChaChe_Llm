namespace LlmWpfPrototype.Models;

public sealed class LightweightBaselineResult
{
    public DateTimeOffset CapturedAt { get; init; }
    public string AssemblyPath { get; init; } = string.Empty;
    public string AssemblyConfiguration { get; init; } = string.Empty;
    public bool AssemblyVerified { get; init; }
    public IReadOnlyList<LightweightMemberBaseline> Members { get; init; } = [];
    public IReadOnlyList<string> Issues { get; init; } = [];

    // Sum of the configured upper and lower chord instances only.
    public decimal? MappedMembersMassKg =>
        AssemblyVerified && Issues.Count == 0 && Members.Count > 0 &&
        Members.All(member => member.MassKg is > 0) &&
        Members.Select(member => member.ComponentName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Members.Count
            ? Members.Sum(member => member.MassKg) : null;
}

public sealed class LightweightMemberBaseline
{
    public string MemberId { get; init; } = string.Empty;
    public string MemberName { get; init; } = string.Empty;
    public string PartPath { get; init; } = string.Empty;
    public string ExpectedComponentName { get; init; } = string.Empty;
    public string ComponentName { get; init; } = string.Empty;
    public string Configuration { get; init; } = string.Empty;
    public string ReferenceStatus { get; init; } = string.Empty;
    public LightweightDimensionObservation OuterWidth { get; init; } = new();
    public LightweightDimensionObservation OuterHeight { get; init; } = new();
    public LightweightDimensionObservation ConfiguredThickness { get; init; } = new();
    public LightweightDimensionObservation InnerWidth { get; init; } = new();
    public LightweightDimensionObservation InnerHeight { get; init; } = new();
    public LightweightDimensionObservation MemberLength { get; init; } = new()
    {
        Status = "not_configured",
        Detail = "配置没有声明杆件长度尺寸；未从文件名、特征名或模型包围盒推断。"
    };
    public decimal? DerivedWallThicknessXmm { get; init; }
    public decimal? DerivedWallThicknessYmm { get; init; }
    public decimal? ConsistentWallThicknessMm { get; init; }
    public string WallThicknessStatus { get; init; } = string.Empty;
    public string MappingNote { get; init; } = string.Empty;
    public bool? HasUnsavedChanges { get; init; }
    public string Material { get; init; } = string.Empty;
    public decimal? MassKg { get; init; }
    public string MassStatus { get; init; } = string.Empty;
}

public sealed class LightweightDimensionObservation
{
    public string DimensionName { get; init; } = string.Empty;
    public string ResolvedDimensionName { get; init; } = string.Empty;
    public string Status { get; init; } = "not_configured";
    public decimal? ValueMm { get; init; }
    public string Detail { get; init; } = string.Empty;
}
