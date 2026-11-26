using System.Globalization;

namespace LlmWpfPrototype.Services.Design;

public enum SectionSafetyDomainTier
{
    InvalidGeometry,
    OutOfBounds,
    SemanticPermissibleOnly,
    GeometricExecutable,
    PracticalNonInterference
}

public sealed record RectangularTubeMechanicsResult(
    decimal WidthMm,
    decimal HeightMm,
    decimal ThicknessMm,
    bool IsGeometryValid,
    decimal InnerWidthMm,
    decimal InnerHeightMm,
    decimal AreaMm2,
    decimal LineMassKgPerM,
    decimal IxMm4,
    decimal IyMm4,
    decimal WxMm3,
    decimal WyMm3,
    decimal SpecificStiffnessIxOverMass,
    decimal MaxFlatWidthToThicknessRatio,
    decimal MassDeltaPercentVsBase,
    decimal IxDeltaPercentVsBase,
    decimal IyDeltaPercentVsBase,
    decimal EffectiveHorizontalWalkwayWidthMm,
    decimal EffectiveInclinedWalkwayWidthMm,
    decimal LinkedUpperRedTubeLengthMm,
    SectionSafetyDomainTier DomainTier,
    string DomainTierLabel,
    string EngineeringNote)
{
    public string SpecText =>
        $"{WidthMm.ToString("0.##", CultureInfo.InvariantCulture)}×{HeightMm.ToString("0.##", CultureInfo.InvariantCulture)}×{ThicknessMm.ToString("0.##", CultureInfo.InvariantCulture)}";

    public string CompactDeltaBadge => !IsGeometryValid
        ? "几何无效"
        : $"{FormatSignedPercent(MassDeltaPercentVsBase)}重 / {FormatSignedPercent(IxDeltaPercentVsBase)}刚";

    public static string FormatSignedPercent(decimal value)
    {
        if (Math.Abs(value) < 0.05m)
        {
            return "0.0%";
        }

        return value > 0
            ? $"+{value.ToString("0.0", CultureInfo.InvariantCulture)}%"
            : $"{value.ToString("0.0", CultureInfo.InvariantCulture)}%";
    }
}

public sealed record StandardSectionCandidateScheme(
    string SpecLabel,
    string StrategyName,
    decimal WidthMm,
    decimal HeightMm,
    decimal ThicknessMm,
    string EngineeringRecommendation);

/// <summary>
/// Deterministic structural mechanics & geometric constraint calculator for Q355B closed rectangular hollow sections.
/// Implements the exact equations and 3-tier safety domain bounds for the non-telescopic single-deck bridge inspection vehicle:
/// - Area A = 2*t*(b + h - 2*t)
/// - Ix = (b*h^3 - (b-2t)*(h-2t)^3) / 12, Iy = (h*b^3 - (h-2t)*(b-2t)^3) / 12
/// - Wx = 2*Ix / h, Wy = 2*Iy / b
/// - Line mass ml = 0.00785 * A (kg/m, Q355B density 7850 kg/m^3)
/// - Walkway clearance W_net = W_nominal - (b - 70)
/// - Upper transverse linked tube L_link = 1280 + (70 - b)
/// </summary>
public static class SectionMechanicsCalculator
{
    public const decimal BaseWidthMm = 70m;
    public const decimal BaseHeightMm = 70m;
    public const decimal BaseThicknessMm = 5m;
    public const decimal SteelDensityKgPerMm3 = 0.00000785m;
    public const decimal NominalHorizontalWalkwayMm = 1270m;
    public const decimal NominalInclinedWalkwayMm = 1200m;
    public const decimal BaseUpperLinkedRedTubeMm = 1280m;

    // 3-tier deformation bounds
    public const decimal PracticalMinOuterMm = 60m;
    public const decimal PracticalMaxOuterMm = 90m;
    public const decimal PracticalMinWallMm = 3m;
    public const decimal PracticalMaxWallMm = 8m;

    public const decimal ExecutableMinOuterMm = 40m;
    public const decimal ExecutableMaxOuterMm = 200m;
    public const decimal ExecutableMinWallMm = 2m;
    public const decimal ExecutableMaxWallMm = 12m;

    public const decimal SemanticMinOuterMm = 20m;
    public const decimal SemanticMaxOuterMm = 500m;
    public const decimal SemanticMinWallMm = 1m;
    public const decimal SemanticMaxWallMm = 40m;

    private static readonly decimal BaseAreaMm2 = 2m * BaseThicknessMm * (BaseWidthMm + BaseHeightMm - 2m * BaseThicknessMm); // 1300 mm^2
    private static readonly decimal BaseLineMassKgPerM = BaseAreaMm2 * 0.00785m; // 10.205 kg/m
    private static readonly decimal BaseIxMm4 = ComputeInertia(BaseWidthMm, BaseHeightMm, BaseThicknessMm); // 920833.33 mm^4

    public static RectangularTubeMechanicsResult BaseSection { get; } =
        CalculateRectangularTube(BaseWidthMm, BaseHeightMm, BaseThicknessMm);

    public static IReadOnlyList<StandardSectionCandidateScheme> StandardSchemes { get; } =
    [
        new("70×70×5", "母型基准方案", 70m, 70m, 5m, "原车标准对称方管基准（10.21 kg/m），双向抗弯与节点法兰完全匹配。"),
        new("60×60×5", "缩廓直接减重（轻量化常用）", 60m, 60m, 5m, "外廓缩小至 60 mm，减重 15.4%（8.64 kg/m），水平通道净宽反而扩大 +10 mm（1280 mm），上弦红管自动补偿为 1290 mm；抗弯惯性矩下降 38.9%，适用于应力富裕区或配合悬臂挠度复核。"),
        new("70×70×4", "减薄壁厚减重（外廓零改动）", 70m, 70m, 4m, "外廓与法兰零变动、直接减重 18.8%（8.29 kg/m），抗弯惯性矩下降 16.4%，轻量化效果显著，需结合仿真校核 7.35 m 大悬臂端静挠度与受压宽厚比。"),
        new("65×65×4", "缩廓减薄深度轻量化", 65m, 65m, 4m, "同时缩廓并减薄壁厚，大幅减重 24.9%（7.66 kg/m），通道净宽扩大 +5 mm；抗弯惯性矩下降 33.4%，建议优先用于次要受力区或经有限元复核后采用。"),
        new("80×80×4", "扩廓减薄·保刚轻量化（控挠优选）", 80m, 80m, 4m, "外廓略增至 80 mm 但壁厚同步减薄至 4 mm，最终仍净减重 6.5%（9.55 kg/m）且抗弯惯性矩反而提升 27.5%；仅在直接缩廓/减薄导致大悬臂挠度超限时推荐，通道净宽缩减 10 mm，红管自动补偿为 1270 mm。"),
        new("60×80×5", "等面异形提刚", 60m, 80m, 5m, "与母型等重（0% 增重），竖向主弯惯性矩提升 22.8%，但横向弱轴下降 22.8%，需核对斜段局部坐标朝向。"),
        new("80×80×5", "增重强化兜底（非轻量化，慎用）", 80m, 80m, 5m, "增重 15.4%，仅在有限元校核证实原截面强度或刚度不足时作为强化兜底，轻量化设计阶段默认不推荐。")
    ];

    public static RectangularTubeMechanicsResult CalculateRectangularTube(decimal widthMm, decimal heightMm, decimal thicknessMm)
    {
        var innerWidth = widthMm - 2m * thicknessMm;
        var innerHeight = heightMm - 2m * thicknessMm;
        var linkedRedTube = BaseUpperLinkedRedTubeMm + (BaseWidthMm - widthMm);
        if (widthMm <= 0m || heightMm <= 0m || thicknessMm <= 0m || innerWidth <= 0m || innerHeight <= 0m || linkedRedTube <= 0m)
        {
            return new RectangularTubeMechanicsResult(
                widthMm, heightMm, thicknessMm,
                IsGeometryValid: false,
                InnerWidthMm: innerWidth,
                InnerHeightMm: innerHeight,
                AreaMm2: 0m,
                LineMassKgPerM: 0m,
                IxMm4: 0m,
                IyMm4: 0m,
                WxMm3: 0m,
                WyMm3: 0m,
                SpecificStiffnessIxOverMass: 0m,
                MaxFlatWidthToThicknessRatio: 0m,
                MassDeltaPercentVsBase: 0m,
                IxDeltaPercentVsBase: 0m,
                IyDeltaPercentVsBase: 0m,
                EffectiveHorizontalWalkwayWidthMm: NominalHorizontalWalkwayMm - (widthMm - BaseWidthMm),
                EffectiveInclinedWalkwayWidthMm: NominalInclinedWalkwayMm - (widthMm - BaseWidthMm),
                LinkedUpperRedTubeLengthMm: linkedRedTube,
                DomainTier: SectionSafetyDomainTier.InvalidGeometry,
                DomainTierLabel: "几何退化/自交（内腔 b-2t 或 h-2t ≤ 0）",
                EngineeringNote: "壁厚过大导致内腔尺寸非正或联动横管长度无效，禁止写入三维模型。");
        }

        var area = 2m * thicknessMm * (widthMm + heightMm - 2m * thicknessMm);
        var lineMass = Math.Round(area * 0.00785m, 3);
        var ix = ComputeInertia(widthMm, heightMm, thicknessMm);
        var iy = ComputeInertia(heightMm, widthMm, thicknessMm);
        var wx = 2m * ix / heightMm;
        var wy = 2m * iy / widthMm;
        var specificStiffness = lineMass > 0m ? (ix / 100000m) / lineMass : 0m;
        var flatRatio = Math.Max(widthMm - 2m * thicknessMm, heightMm - 2m * thicknessMm) / thicknessMm;

        var massDeltaPct = Math.Round((area - BaseAreaMm2) / BaseAreaMm2 * 100m, 1);
        var ixDeltaPct = Math.Round((ix - BaseIxMm4) / BaseIxMm4 * 100m, 1);
        var iyDeltaPct = Math.Round((iy - BaseIxMm4) / BaseIxMm4 * 100m, 1);

        var horizontalWalkway = NominalHorizontalWalkwayMm - (widthMm - BaseWidthMm);
        var inclinedWalkway = NominalInclinedWalkwayMm - (widthMm - BaseWidthMm);

        var (tier, tierLabel, note) = EvaluateDomainTier(widthMm, heightMm, thicknessMm, flatRatio, horizontalWalkway);

        return new RectangularTubeMechanicsResult(
            WidthMm: widthMm,
            HeightMm: heightMm,
            ThicknessMm: thicknessMm,
            IsGeometryValid: true,
            InnerWidthMm: innerWidth,
            InnerHeightMm: innerHeight,
            AreaMm2: Math.Round(area, 2),
            LineMassKgPerM: lineMass,
            IxMm4: Math.Round(ix, 1),
            IyMm4: Math.Round(iy, 1),
            WxMm3: Math.Round(wx, 1),
            WyMm3: Math.Round(wy, 1),
            SpecificStiffnessIxOverMass: Math.Round(specificStiffness, 3),
            MaxFlatWidthToThicknessRatio: Math.Round(flatRatio, 1),
            MassDeltaPercentVsBase: massDeltaPct,
            IxDeltaPercentVsBase: ixDeltaPct,
            IyDeltaPercentVsBase: iyDeltaPct,
            EffectiveHorizontalWalkwayWidthMm: horizontalWalkway,
            EffectiveInclinedWalkwayWidthMm: inclinedWalkway,
            LinkedUpperRedTubeLengthMm: linkedRedTube,
            DomainTier: tier,
            DomainTierLabel: tierLabel,
            EngineeringNote: note);
    }

    private static decimal ComputeInertia(decimal width, decimal height, decimal thickness)
    {
        var innerW = width - 2m * thickness;
        var innerH = height - 2m * thickness;
        var outerTerm = width * height * height * height;
        var innerTerm = innerW * innerH * innerH * innerH;
        return (outerTerm - innerTerm) / 12m;
    }

    private static (SectionSafetyDomainTier Tier, string Label, string Note) EvaluateDomainTier(
        decimal width,
        decimal height,
        decimal thickness,
        decimal flatWidthToThickness,
        decimal horizontalWalkway)
    {
        var inExecutable = width >= ExecutableMinOuterMm && width <= ExecutableMaxOuterMm &&
                           height >= ExecutableMinOuterMm && height <= ExecutableMaxOuterMm &&
                           thickness >= ExecutableMinWallMm && thickness <= ExecutableMaxWallMm;
        if (inExecutable)
        {
            var notes = new List<string>();
            if (width < BaseWidthMm || height < BaseHeightMm || thickness < BaseThicknessMm)
            {
                notes.Add($"相对母型属于轻量化探索方向（平板宽厚比约 {flatWidthToThickness:0.#}），具体可行性应以 ANSYS APDL 仿真校核的应力、7.35 m 悬臂静挠度与受压稳定结果为准");
            }
            else if (width > BaseWidthMm || height > BaseHeightMm || thickness > BaseThicknessMm)
            {
                notes.Add($"相对母型属于截面加强方向（水平通道有效净宽约 {horizontalWalkway:0.#} mm），建议结合仿真校核结果权衡自重增量，并复核法兰螺栓空间与踏步间隙");
            }
            else
            {
                notes.Add("与母型基准 70×70×5 mm 一致，可作为轻量化或加强仿真对比的基准方案");
            }

            return (
                SectionSafetyDomainTier.GeometricExecutable,
                "几何有效（最终适用性以仿真校核为准）",
                string.Join("；", notes) + "。");
        }

        var inSemantic = width >= SemanticMinOuterMm && width <= SemanticMaxOuterMm &&
                         height >= SemanticMinOuterMm && height <= SemanticMaxOuterMm &&
                         thickness >= SemanticMinWallMm && thickness <= SemanticMaxWallMm;
        if (inSemantic)
        {
            return (
                SectionSafetyDomainTier.SemanticPermissibleOnly,
                "超出当前三维参数化模板可重建边界",
                "该规格超出了当前参数化三维模板允许的重建范围，系统会在改模前拦截；可先在仿真中探索规律后再收敛回可重建尺寸。");
        }

        return (
            SectionSafetyDomainTier.OutOfBounds,
            "参数超出有效重建范围",
            "该尺寸超出当前参数化模型的有效重建边界，请调整后结合仿真校核。");
    }
}
