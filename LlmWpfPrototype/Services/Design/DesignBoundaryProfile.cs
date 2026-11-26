using System.IO;
using System.Text.Json;

namespace LlmWpfPrototype.Services.Design;

public sealed class DesignBoundaryProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileName { get; set; } = "不伸缩单层轨行悬挂式桥梁检查车—多规范绑定设计边界配置";
    public string Description { get; set; } = string.Empty;
    public VehicleModelBaselineProfile Vehicle { get; set; } = new();
    public GbT3811BoundaryProfile GbT3811 { get; set; } = new();
    public Gb50017BoundaryProfile Gb50017 { get; set; } = new();
    public JtT1430BoundaryProfile JtT1430 { get; set; } = new();

    public static DesignBoundaryProfile CreateDefault() => new();
}

public sealed class VehicleModelBaselineProfile
{
    public string VehicleName { get; set; } = "单层不可伸缩轨行悬挂式桥梁检查车";
    public string StandardModelCode { get; set; } = "JC-GD-NG";
    public string TopologySummary { get; set; } = "U型对称空间格构桁架（梁底 28b 工字钢固定双轨悬挂、固定桁架不过墩）";
    public decimal TotalSpanMm { get; set; } = 18896m;
    public decimal TotalHeightMm { get; set; } = 8632m;
    public decimal GantryRailCenterSpacingMm { get; set; } = 4200m;
    public decimal HorizontalLowerChordLengthMm { get; set; } = 7991m;
    public decimal InclinedTrussAngleDeg { get; set; } = 32m;
    public decimal CantileverLengthMm { get; set; } = 7350m;
    public decimal MainTrussCenterSpacingMm { get; set; } = 1350m;
    public decimal NominalHorizontalWalkwayWidthMm { get; set; } = 1270m;
    public decimal NominalInclinedWalkwayWidthMm { get; set; } = 1200m;
    public decimal BaseUpperLinkedRedTubeLengthMm { get; set; } = 1280m;
    public decimal BaselineWidthMm { get; set; } = 70m;
    public decimal BaselineHeightMm { get; set; } = 70m;
    public decimal BaselineThicknessMm { get; set; } = 5m;
    public string MaterialGrade { get; set; } = "Q355B";
    public decimal YieldStrengthMpa { get; set; } = 355m;
    public decimal TensileStrengthMinMpa { get; set; } = 470m;
    public decimal ElasticModulusMpa { get; set; } = 206000m;
    public decimal DensityKgPerM3 { get; set; } = 7850m;
    public decimal RatedDistributedLiveLoadKg { get; set; } = 1600m;
    public decimal MiddlePlatformMaxLoadKg { get; set; } = 800m;
    public decimal EachEndMaxLoadKg { get; set; } = 400m;
    public decimal DesignMaxWorkingWindSpeedMps { get; set; } = 12.0m;
    public string DefaultCorrosionEnvironment { get; set; } = "Moderate";
    public decimal TemplateMinOuterMm { get; set; } = 40m;
    public decimal TemplateMaxOuterMm { get; set; } = 200m;
    public decimal TemplateMinWallMm { get; set; } = 2m;
    public decimal TemplateMaxWallMm { get; set; } = 12m;
    public string UpperChordModelNote { get; set; } =
        "在 7.35 m 大悬臂受弯下斜桁架段以受拉为主（门架支座与折线法兰处兼受弯矩）；修改外宽 b 时上弦横向联动红管按 L=1280+(70-b) 自动补偿，且同步影响车内通行净宽 W_net=1270-(b-70)。";
    public string LowerChordModelNote { get; set; } =
        "在 7.35 m 大悬臂受弯下斜桁架段以受压为主（含 6.575 m / 6.375 m 主弦杆及折线过渡段，对外统一称“下弦杆”）；减薄壁厚或缩减外廓时首要风险为受压局部屈曲（宽厚比 b0/t）与压弯整体稳定（长细比 λ）。";
}

public sealed class GbT3811BoundaryProfile
{
    public string StandardCode { get; set; } = "GB/T 3811-2008";
    public decimal ClosedSectionMinWallMildMm { get; set; } = 2.5m;
    public decimal ClosedSectionMinWallModerateMm { get; set; } = 4.0m;
    public decimal ClosedSectionMinWallSevereMm { get; set; } = 6.0m;
    public decimal ThinPlateWeldThresholdMm { get; set; } = 4.0m;
    public decimal LowAlloyMaxDynamicWeldThicknessMm { get; set; } = 35.0m;
    public decimal SafetyFactorComboA { get; set; } = 1.48m;
    public decimal SafetyFactorComboB { get; set; } = 1.34m;
    public decimal SafetyFactorComboC { get; set; } = 1.22m;
    public decimal HighStrengthYieldRatioThreshold { get; set; } = 0.70m;
    public int ChordCompressionMaxSlenderness { get; set; } = 150;
    public int ChordTensionMaxSlenderness { get; set; } = 180;
    public int WebCompressionMaxSlenderness { get; set; } = 200;
    public int WebTensionMaxSlenderness { get; set; } = 250;
    public int CantileverDeflectionDivisor { get; set; } = 350;
    public decimal StaticTestLoadFactor { get; set; } = 1.25m;
    public decimal DynamicTestLoadFactor { get; set; } = 1.10m;
}

public sealed class Gb50017BoundaryProfile
{
    public string StandardCode { get; set; } = "GB 50017-2017";
    public decimal EpsilonKReferenceFyMpa { get; set; } = 235.0m;
    public decimal BoxBeamColumnS1Factor { get; set; } = 30.0m;
    public decimal BoxBeamColumnS2Factor { get; set; } = 35.0m;
    public decimal BoxBeamColumnS3Factor { get; set; } = 40.0m;
    public decimal BoxBeamColumnS4Factor { get; set; } = 45.0m;
    public decimal AxialCompressionLocalStabilityFactor { get; set; } = 40.0m;
    public decimal EffectiveWidthReductionThresholdFactor { get; set; } = 42.0m;
    public decimal ColumnCurveCToBSplitRatio { get; set; } = 20.0m;
    public decimal TubeJointMaxOuterWidthThicknessFactor { get; set; } = 40.0m;
    public decimal TubeJointGapChordMaxRatio { get; set; } = 35.0m;
    public int TrussCompressionMaxSlenderness { get; set; } = 150;
    public int TrussDynamicWebMaxSlenderness { get; set; } = 120;
    public int TrussTensionDynamicMaxSlenderness { get; set; } = 250;
}

public sealed class JtT1430BoundaryProfile
{
    public string StandardCode { get; set; } = "JT/T 1430-2022";
    public decimal MinWalkwayClearWidthMm { get; set; } = 800m;
    public decimal MaxWalkwayClearWidthMm { get; set; } = 3000m;
    public decimal MinWalkwayClearHeightMm { get; set; } = 1800m;
    public decimal MaxWalkwayClearHeightMm { get; set; } = 2200m;
    public int SteelMidspanDeflectionDivisor { get; set; } = 800;
    public int SteelCantileverDeflectionDivisor { get; set; } = 400;
    public decimal MinConcentratedLoadKn { get; set; } = 3.5m;
    public decimal MinTotalRatedLoadKn { get; set; } = 15.0m;
    public decimal MinWorkingWindSpeedMps { get; set; } = 13.8m;
    public decimal MinGuardrailHeightMm { get; set; } = 1200m;
    public decimal MinToeboardHeightMm { get; set; } = 150m;
    public decimal DynamicTestLoadFactor { get; set; } = 1.25m;
    public decimal StaticCapacityTestLoadFactor { get; set; } = 1.50m;
}

/// <summary>
/// Loads the externalized Config/design-boundary-profile.json with timestamp-based caching
/// so updates to vehicle parameters or standard boundaries take effect automatically without recompiling.
/// </summary>
public static class DesignBoundaryProfileLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly object Sync = new();
    private static string? _cachedPath;
    private static DateTime _cachedWriteTimeUtc;
    private static DesignBoundaryProfile? _cachedProfile;

    public static DesignBoundaryProfile Load(string? explicitPath = null)
    {
        var path = ResolveProfilePath(explicitPath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return DesignBoundaryProfile.CreateDefault();
        }

        try
        {
            var writeTime = File.GetLastWriteTimeUtc(path);
            lock (Sync)
            {
                if (_cachedProfile is not null &&
                    string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                    _cachedWriteTimeUtc == writeTime)
                {
                    return _cachedProfile;
                }

                var raw = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<DesignBoundaryProfile>(raw, JsonOptions)
                             ?? DesignBoundaryProfile.CreateDefault();
                parsed.Vehicle ??= new VehicleModelBaselineProfile();
                parsed.GbT3811 ??= new GbT3811BoundaryProfile();
                parsed.Gb50017 ??= new Gb50017BoundaryProfile();
                parsed.JtT1430 ??= new JtT1430BoundaryProfile();

                _cachedPath = path;
                _cachedWriteTimeUtc = writeTime;
                _cachedProfile = parsed;
                return parsed;
            }
        }
        catch
        {
            return DesignBoundaryProfile.CreateDefault();
        }
    }

    public static string? ResolveProfilePath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var appBaseCandidate = Path.Combine(AppContext.BaseDirectory, "Config", "design-boundary-profile.json");
        if (File.Exists(appBaseCandidate))
        {
            return appBaseCandidate;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "Config", "design-boundary-profile.json");
            if (File.Exists(direct))
            {
                return direct;
            }

            var nested = Path.Combine(dir.FullName, "LlmWpfPrototype", "Config", "design-boundary-profile.json");
            if (File.Exists(nested))
            {
                return nested;
            }
        }

        return null;
    }
}
