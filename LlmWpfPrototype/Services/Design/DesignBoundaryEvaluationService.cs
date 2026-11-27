using System.Globalization;
using System.Text;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services.Design;

public enum DesignBoundaryMemberRole
{
    Unspecified,
    UpperChord,
    LowerChord
}

public enum DesignBoundaryRiskLevel
{
    Ok,
    Advisory,
    Warning,
    InvalidGeometry
}

public sealed record DesignBoundaryEvaluationResult(
    DesignBoundaryMemberRole MemberRole,
    string MemberDisplayName,
    RectangularTubeMechanicsResult Mechanics,
    decimal? CurrentWidthMm,
    decimal? CurrentHeightMm,
    decimal? CurrentThicknessMm,
    decimal EpsilonK,
    decimal NetFlatWidthThicknessRatio,
    decimal OuterWidthThicknessRatio,
    decimal MinGyrationRadiusMm,
    decimal BaselineMinGyrationRadiusMm,
    string SectionWidthThicknessClass,
    string ColumnCurveClass,
    bool ExceedsAxialLocalBucklingLimit,
    bool NeedsEffectiveWidthReduction,
    bool ExceedsTubeJointOuterRatioLimit,
    DesignBoundaryRiskLevel CorrosionWallRisk,
    string CorrosionWallSummary,
    string WidthThicknessSummary,
    string ModelLinkageSummary,
    string SimulationChecklistSummary,
    IReadOnlyList<string> Highlights,
    GbT6728RoundedSectionResult? RoundedSection = null,
    string StandardSectionSummary = "",
    string WeldingDetailingSummary = "",
    string SteelMaterialSummary = "")
{
    public bool HasBoundaryWarning =>
        !Mechanics.IsGeometryValid ||
        CorrosionWallRisk is DesignBoundaryRiskLevel.Warning or DesignBoundaryRiskLevel.InvalidGeometry ||
        ExceedsAxialLocalBucklingLimit ||
        ExceedsTubeJointOuterRatioLimit;
}

/// <summary>
/// Deterministic, model-anchored multi-standard boundary evaluator binding the active bridge inspection
/// vehicle SolidWorks geometry to GB/T 3811-2008, GB 50017-2017, and JT/T 1430-2022.
/// Strictly emits structured engineering facts with standard clause citations and NEVER emits raw
/// RAG [E#] markers, verified/*.md filenames, or unverified pre-FEA weight/stiffness percentages.
/// </summary>
public static class DesignBoundaryEvaluationService
{
    public static DesignBoundaryEvaluationResult EvaluateMemberSection(
        decimal widthMm,
        decimal heightMm,
        decimal thicknessMm,
        DesignBoundaryMemberRole memberRole,
        MemberSectionSpecRecord? currentMemberSpec = null,
        DesignBoundaryProfile? profile = null)
    {
        var activeProfile = profile ?? DesignBoundaryProfileLoader.Load();
        var vehicle = activeProfile.Vehicle;
        var gb3811 = activeProfile.GbT3811;
        var gb50017 = activeProfile.Gb50017;
        var jtt1430 = activeProfile.JtT1430;

        var memberName = memberRole switch
        {
            DesignBoundaryMemberRole.UpperChord => "上弦杆",
            DesignBoundaryMemberRole.LowerChord => "下弦杆",
            _ => "桁架主弦杆"
        };

        var calc = SectionMechanicsCalculator.CalculateRectangularTube(widthMm, heightMm, thicknessMm);
        var baselineCalc = SectionMechanicsCalculator.CalculateRectangularTube(
            vehicle.BaselineWidthMm,
            vehicle.BaselineHeightMm,
            vehicle.BaselineThicknessMm);

        var epsilonKRaw = (decimal)Math.Sqrt((double)(gb50017.EpsilonKReferenceFyMpa / Math.Max(1m, vehicle.YieldStrengthMpa)));
        var epsilonK = Math.Round(epsilonKRaw, 3, MidpointRounding.AwayFromZero);
        var baseRMin = ComputeMinGyrationRadiusMm(baselineCalc);

        if (!calc.IsGeometryValid)
        {
            return new DesignBoundaryEvaluationResult(
                MemberRole: memberRole,
                MemberDisplayName: memberName,
                Mechanics: calc,
                CurrentWidthMm: currentMemberSpec?.WidthMm,
                CurrentHeightMm: currentMemberSpec?.HeightMm,
                CurrentThicknessMm: currentMemberSpec?.ThicknessMm,
                EpsilonK: epsilonK,
                NetFlatWidthThicknessRatio: 0m,
                OuterWidthThicknessRatio: 0m,
                MinGyrationRadiusMm: 0m,
                BaselineMinGyrationRadiusMm: baseRMin,
                SectionWidthThicknessClass: "几何无效",
                ColumnCurveClass: "无",
                ExceedsAxialLocalBucklingLimit: true,
                NeedsEffectiveWidthReduction: true,
                ExceedsTubeJointOuterRatioLimit: true,
                CorrosionWallRisk: DesignBoundaryRiskLevel.InvalidGeometry,
                CorrosionWallSummary: "内腔尺寸非正（b-2t ≤ 0 或 h-2t ≤ 0）或联动横管长度无效，无法构建闭口方管。",
                WidthThicknessSummary: "截面几何无效，无法计算宽厚比。",
                ModelLinkageSummary: "该尺寸会导致 SolidWorks 实体退化/自交，禁止写入三维模型。",
                SimulationChecklistSummary: "请先调整至有效空心闭口尺寸。",
                Highlights: ["几何退化：闭口方管内腔尺寸非正或联动长度无效，禁止写入模型。"]);
        }

        var netRatio = calc.MaxFlatWidthToThicknessRatio;
        var outerRatio = Math.Round(Math.Max(widthMm, heightMm) / thicknessMm, 1);
        var rMin = ComputeMinGyrationRadiusMm(calc);

        // 1. GB 50017-2017 width-to-thickness classification & local buckling / tube joint limits
        var s1Limit = Math.Round(gb50017.BoxBeamColumnS1Factor * epsilonKRaw, 1, MidpointRounding.AwayFromZero);
        var s2Limit = Math.Round(gb50017.BoxBeamColumnS2Factor * epsilonKRaw, 1, MidpointRounding.AwayFromZero);
        var s3Limit = Math.Round(gb50017.BoxBeamColumnS3Factor * epsilonKRaw, 1, MidpointRounding.AwayFromZero);
        var s4Limit = Math.Round(gb50017.BoxBeamColumnS4Factor * epsilonKRaw, 1, MidpointRounding.AwayFromZero);
        var axialLimit = Math.Round(gb50017.AxialCompressionLocalStabilityFactor * epsilonKRaw, 1, MidpointRounding.AwayFromZero);
        var effReductionLimit = Math.Round(gb50017.EffectiveWidthReductionThresholdFactor * epsilonKRaw, 1, MidpointRounding.AwayFromZero);
        var jointOuterLimit = Math.Round(gb50017.TubeJointMaxOuterWidthThicknessFactor * epsilonKRaw, 1, MidpointRounding.AwayFromZero);

        var sectionClass = netRatio <= s1Limit ? "S1 级"
            : netRatio <= s2Limit ? "S2 级"
            : netRatio <= s3Limit ? "S3 级"
            : netRatio <= s4Limit ? "S4 级"
            : "超出 S4 级（S5 薄壁）";

        var columnCurveClass = netRatio <= gb50017.ColumnCurveCToBSplitRatio ? "c 类" : "b 类";
        var exceedsAxial = netRatio > axialLimit;
        var needsEffWidth = netRatio > effReductionLimit;
        var exceedsJointOuter = outerRatio > jointOuterLimit || outerRatio > gb50017.TubeJointGapChordMaxRatio;

        var widthThicknessSb = new StringBuilder();
        widthThicknessSb.Append(
            $"{vehicle.MaterialGrade}（εk≈{epsilonK:0.000}）净宽厚比 b₀/t={netRatio:0.#}（{gb50017.StandardCode} 表3.5.1 压弯 {sectionClass}，表7.2.1-1 属 {columnCurveClass}截面）");
        if (exceedsAxial)
        {
            widthThicknessSb.Append(
                $"，已超过轴心受压不局部失稳限值 40εk≈{axialLimit:0.#}（7.3.1）");
            if (needsEffWidth)
            {
                widthThicknessSb.Append($"及有效宽度折减阈值 42εk≈{effReductionLimit:0.#}（7.3.4），受压壁板存在明显局部鼓曲风险");
            }
        }
        else
        {
            widthThicknessSb.Append($"，满足轴心受压局部稳定限值 ≤40εk（≈{axialLimit:0.#}，7.3.1）");
        }

        if (exceedsJointOuter)
        {
            widthThicknessSb.Append(
                $"；外缘宽厚比 b/t={outerRatio:0.#} 超出直接焊接矩形管节点限值（13.1.2 40εk≈{jointOuterLimit:0.#} / 表13.4.1 ≤{gb50017.TubeJointGapChordMaxRatio:0.#}），节点局部冲剪或管壁压屈风险偏高");
        }
        else
        {
            widthThicknessSb.Append($"；外缘 b/t={outerRatio:0.#} 满足矩形管节点 13.1.2 限值（≤{jointOuterLimit:0.#}）");
        }

        var widthThicknessSummary = widthThicknessSb.ToString();

        // 2. GB/T 3811-2008 Table 34 corrosion minimum thickness & welding detailing
        DesignBoundaryRiskLevel wallRisk;
        string wallSummary;
        if (thicknessMm < gb3811.ClosedSectionMinWallMildMm)
        {
            wallRisk = DesignBoundaryRiskLevel.Warning;
            wallSummary =
                $"壁厚 {thicknessMm:0.##} mm 低于 {gb3811.StandardCode} 表34 闭口截面微弱腐蚀下限（{gb3811.ClosedSectionMinWallMildMm:0.#} mm），且远低于一般露天桥梁中等腐蚀限值（{gb3811.ClosedSectionMinWallModerateMm:0.#} mm），不建议作为主要承载桁架弦杆长期服役";
        }
        else if (thicknessMm < gb3811.ClosedSectionMinWallModerateMm)
        {
            wallRisk = DesignBoundaryRiskLevel.Warning;
            wallSummary =
                $"壁厚 {thicknessMm:0.##} mm 低于 {gb3811.StandardCode} 表34 中等腐蚀闭口截面最小壁厚（{gb3811.ClosedSectionMinWallModerateMm:0.#} mm，仅微弱腐蚀且牢靠防锈涂漆时可低至 {gb3811.ClosedSectionMinWallMildMm:0.#} mm）；露天桥梁检查车按 {jtt1430.StandardCode} 5.3.1.3 须做重防腐，且壁厚 < {gb3811.ThinPlateWeldThresholdMm:0.#} mm 时角焊缝焊脚等于板厚（5.9.2.2），需同步核对腐蚀裕量与焊接工艺";
        }
        else if (thicknessMm < gb3811.ClosedSectionMinWallSevereMm)
        {
            wallRisk = DesignBoundaryRiskLevel.Ok;
            wallSummary =
                $"壁厚 {thicknessMm:0.##} mm 满足 {gb3811.StandardCode} 表34 中等腐蚀闭口截面最小壁厚（≥{gb3811.ClosedSectionMinWallModerateMm:0.#} mm，配合 {jtt1430.StandardCode} 5.3.1.3 重防腐处理；若服役于跨海/融雪剂等严重腐蚀环境，表34 要求 ≥{gb3811.ClosedSectionMinWallSevereMm:0.#} mm）";
        }
        else
        {
            wallRisk = DesignBoundaryRiskLevel.Ok;
            wallSummary =
                $"壁厚 {thicknessMm:0.##} mm 满足 {gb3811.StandardCode} 表34 严重腐蚀闭口截面最小壁厚（≥{gb3811.ClosedSectionMinWallSevereMm:0.#} mm）且满足低合金钢动载焊接厚板不宜超 {gb3811.LowAlloyMaxDynamicWeldThicknessMm:0.#} mm 的 5.9.1.4 要求";
        }

        // 3. Model-anchored geometry linkage & mating checks (THIS vehicle model!)
        var linkageParts = new List<string>();
        var currentW = currentMemberSpec?.WidthMm ?? vehicle.BaselineWidthMm;
        var currentH = currentMemberSpec?.HeightMm ?? vehicle.BaselineHeightMm;
        var currentT = currentMemberSpec?.ThicknessMm ?? vehicle.BaselineThicknessMm;
        var onlyWallChangedVsCurrent = currentMemberSpec is not null &&
                                       widthMm == currentW &&
                                       heightMm == currentH &&
                                       thicknessMm != currentT;

        if (onlyWallChangedVsCurrent)
        {
            var preservedLinkage = memberRole == DesignBoundaryMemberRole.UpperChord
                ? $"（保持上弦横向联动红管 {calc.LinkedUpperRedTubeLengthMm:0.#} mm、水平/斜段通道有效净宽 {calc.EffectiveHorizontalWalkwayWidthMm:0.#} / {calc.EffectiveInclinedWalkwayWidthMm:0.#} mm 零变动）"
                : $"（保持水平/斜段通道有效净宽 {calc.EffectiveHorizontalWalkwayWidthMm:0.#} / {calc.EffectiveInclinedWalkwayWidthMm:0.#} mm 零变动）";
            linkageParts.Add($"相对当前模型仅调整壁厚，方管外廓与折线法兰/节点外形保持不变{preservedLinkage}");
        }
        else if (memberRole == DesignBoundaryMemberRole.UpperChord)
        {
            linkageParts.Add(
                $"上弦横向联动红管将自动补偿至 {calc.LinkedUpperRedTubeLengthMm:0.#} mm（公式 L=1280+(70-b)），水平/斜段通道有效净宽为 {calc.EffectiveHorizontalWalkwayWidthMm:0.#} / {calc.EffectiveInclinedWalkwayWidthMm:0.#} mm");
        }
        else if (memberRole == DesignBoundaryMemberRole.LowerChord)
        {
            linkageParts.Add(
                $"下弦杆外宽 {widthMm:0.##} mm 同步影响下部桁架法兰连接与踏步空间（参考水平通道净宽 {calc.EffectiveHorizontalWalkwayWidthMm:0.#} mm）");
        }
        else
        {
            linkageParts.Add(
                $"水平/斜段通道有效净宽约 {calc.EffectiveHorizontalWalkwayWidthMm:0.#} / {calc.EffectiveInclinedWalkwayWidthMm:0.#} mm；若用于上弦杆，横向联动红管自动补偿至 {calc.LinkedUpperRedTubeLengthMm:0.#} mm");
        }

        var walkwayMeetsJtt = calc.EffectiveHorizontalWalkwayWidthMm >= jtt1430.MinWalkwayClearWidthMm &&
                              calc.EffectiveHorizontalWalkwayWidthMm <= jtt1430.MaxWalkwayClearWidthMm &&
                              calc.EffectiveInclinedWalkwayWidthMm >= jtt1430.MinWalkwayClearWidthMm;
        linkageParts.Add(walkwayMeetsJtt
            ? $"满足 {jtt1430.StandardCode} 表2 桁架净宽要求（{jtt1430.MinWalkwayClearWidthMm:0.#}~{jtt1430.MaxWalkwayClearWidthMm:0.#} mm）"
            : $"通道净宽超出 {jtt1430.StandardCode} 表2 规定范围（{jtt1430.MinWalkwayClearWidthMm:0.#}~{jtt1430.MaxWalkwayClearWidthMm:0.#} mm），存在明显通行净空风险");

        if (widthMm < vehicle.BaselineWidthMm || heightMm < vehicle.BaselineHeightMm || thicknessMm < vehicle.BaselineThicknessMm)
        {
            linkageParts.Add(
                $"因尺寸或壁厚小于母型 {vehicle.BaselineWidthMm:0.#}×{vehicle.BaselineHeightMm:0.#}×{vehicle.BaselineThicknessMm:0.#} mm，须按 {gb50017.StandardCode} 13.2.1 核对本车主管外尺寸与壁厚仍不小于相连腹杆/横管支管");
        }
        else if (widthMm > vehicle.BaselineWidthMm || heightMm > vehicle.BaselineHeightMm)
        {
            linkageParts.Add(
                $"因外廓大于母型 {vehicle.BaselineWidthMm:0.#}×{vehicle.BaselineHeightMm:0.#} mm，需核对本车折线法兰螺栓边距/间距（{gb3811.StandardCode} 表36）及走道踏步间隙");
        }

        var rTrend = rMin < baseRMin - 0.1m
            ? $"截面最小回转半径 r_min={rMin:0.0} mm（小于母型 {baseRMin:0.0} mm，会推高长细比 λ=l_c/r）"
            : rMin > baseRMin + 0.1m
                ? $"截面最小回转半径 r_min={rMin:0.0} mm（大于母型 {baseRMin:0.0} mm，有利于降低长细比 λ=l_c/r）"
                : $"截面最小回转半径 r_min={rMin:0.0} mm（与母型 {baseRMin:0.0} mm 基本一致）";
        linkageParts.Add(rTrend);

        var modelLinkageSummary = string.Join("；", linkageParts);

        // 4. Simulation verification redlines anchored to THIS 7.35 m cantilever Q355B vehicle
        var cantileverLimitJtt = Math.Round(vehicle.CantileverLengthMm / jtt1430.SteelCantileverDeflectionDivisor, 1, MidpointRounding.AwayFromZero);
        var cantileverLimit3811 = Math.Round(vehicle.CantileverLengthMm / gb3811.CantileverDeflectionDivisor, 1, MidpointRounding.AwayFromZero);
        var midspanLimitJtt = Math.Round(vehicle.GantryRailCenterSpacingMm / jtt1430.SteelMidspanDeflectionDivisor, 1, MidpointRounding.AwayFromZero);
        var allowableComboA = ComputeAllowableStressMpa(vehicle, gb3811, gb3811.SafetyFactorComboA);
        var allowableComboB = ComputeAllowableStressMpa(vehicle, gb3811, gb3811.SafetyFactorComboB);

        var roleFocus = memberRole switch
        {
            DesignBoundaryMemberRole.UpperChord =>
                $"上弦杆在 {vehicle.CantileverLengthMm / 1000m:0.00} m 斜桁架悬臂段以受拉为主（门架支座与折线法兰处兼受弯），重点核对受拉长细比 ≤{gb3811.ChordTensionMaxSlenderness}（受压区 ≤{gb3811.ChordCompressionMaxSlenderness}，{gb3811.StandardCode} 表28）与节点焊缝疲劳",
            DesignBoundaryMemberRole.LowerChord =>
                $"下弦杆在 {vehicle.CantileverLengthMm / 1000m:0.00} m 斜桁架悬臂段以受压为主，重点核对受压长细比 ≤{gb3811.ChordCompressionMaxSlenderness}（{gb3811.StandardCode} 表28 / {gb50017.StandardCode} 表7.4.6）与压弯整体/局部屈曲稳定",
            _ =>
                $"主弦杆须按 {gb3811.StandardCode} 表28 复核受压长细比 ≤{gb3811.ChordCompressionMaxSlenderness}、受拉 ≤{gb3811.ChordTensionMaxSlenderness}（斜桁架上弦受拉、下弦受压）"
        };

        var simulationChecklistSummary =
            $"{roleFocus}；后续 ANSYS APDL 仿真必复核：① {vehicle.CantileverLengthMm / 1000m:0.00} m 悬臂端静挠度 ≤{cantileverLimitJtt:0.#} mm（{jtt1430.StandardCode} 表3 L'/400，严于 {gb3811.StandardCode} L₁/350≈{cantileverLimit3811:0.#} mm）及 {vehicle.GantryRailCenterSpacingMm / 1000m:0.00} m 跨中挠度 ≤{midspanLimitJtt:0.#} mm（L/800）；② {vehicle.MaterialGrade} 静强度许用应力（{gb3811.StandardCode} 表22/式28 保守值：组合A ≈{allowableComboA:0.#} MPa、组合B ≈{allowableComboB:0.#} MPa）；③ {jtt1430.StandardCode} 附录A {jtt1430.DynamicTestLoadFactor:0.##} 倍动载与 {jtt1430.StaticCapacityTestLoadFactor:0.##} 倍静载承载工况（卸载无残余变形）。";

        // 5. GB/T 6728-2025 standard hollow section catalog + rounded-corner mechanics + Table B.1 wall tolerance
        var rounded = GbT6728SectionCatalogService.Evaluate(widthMm, heightMm, thicknessMm);
        var catalogStatus = rounded.IsStandardCatalogSpec
            ? "属 GB/T 6728-2025 表1/表2 标准冷弯闭口型钢规格（可直接常规采购）"
            : rounded.NearestStandardSpecs.Count > 0
                ? $"非 GB/T 6728-2025 表1/表2 常用标准规格（须协议订货，最接近国标推荐：{string.Join(" 或 ", rounded.NearestStandardSpecs)}）"
                : "非 GB/T 6728-2025 常用标准规格（须协议订货）";
        var negativeTolNote = rounded.MinAllowableWallMm < gb3811.ClosedSectionMinWallModerateMm
            ? $"（注意：扣减表B.1 负偏差 -{rounded.WallToleranceMm:0.00} mm 后实测下限 {rounded.MinAllowableWallMm:0.00} mm < 4 mm，入厂复检须关注壁厚下限与防腐裕量）"
            : $"（表B.1 壁厚偏差 ±{rounded.WallToleranceMm:0.00} mm，负偏差下限 {rounded.MinAllowableWallMm:0.00} mm）";
        var standardSectionSummary =
            $"{catalogStatus}；计入公称外圆弧 R={rounded.NominalOuterRadiusMm:0.#} mm（表4 Q355B 范围 {rounded.LowAlloyCornerRadiusRangeText}）后精确特性：A={rounded.RoundedAreaCm2:0.000} cm²、M={rounded.RoundedLineMassKgPerM:0.000} kg/m、Ix={rounded.RoundedIxCm4:0.000} cm⁴{negativeTolNote}";

        // 6. GB 50661-2011 welding detailing & minimum fillet / thickness transition checks
        var dynMinFillet = thicknessMm <= 6m ? Math.Min(5m, thicknessMm) : (thicknessMm <= 12m ? 5m : 6m);
        var edgeMaxFilletText = thicknessMm <= 6m
            ? $"沿母材棱边最大焊脚 ≤t={thicknessMm:0.##} mm（5.4.3）"
            : $"沿母材棱边最大焊脚 ≤t-(1~2) mm={thicknessMm - 2m:0.##}~{thicknessMm - 1m:0.##} mm（5.4.3）";
        var transitionAllowedDiff = thicknessMm < 5m ? 2m : (thicknessMm <= 9m ? 2m : (thicknessMm <= 12m ? 3m : 4m));
        var weldingDetailingSummary =
            $"动载桁架角焊缝最小焊脚 hf≥{dynMinFillet:0.##} mm（表5.4.2，不超过较薄板厚，静载 t≤6 mm 可为 3 mm）、{edgeMaxFilletText}；相贯节点角焊缝计算厚度按 he=min(tc, 1.07tb) 核对（表5.3.6-2）；与折线法兰或厚板对接若厚度差 >{transitionAllowedDiff:0.#} mm 须按 1:2.5 平缓过渡（表5.4.4）";

        // 7. GB/T 1591-2018 Q355 steel grade, CEV & low-temperature impact check
        var yieldByThickness = thicknessMm <= 16m ? "≥355 MPa" : "≥345 MPa";
        var steelMaterialSummary =
            $"{vehicle.MaterialGrade}（t={thicknessMm:0.##} mm，GB/T 1591-2018 表7 上屈服 ReH {yieldByThickness}、表2 碳当量 CEV≤0.45%）；常温/温暖地区采用 Q355B（表11 +20℃ KV2≥34 J），若服役桥位冬季最低温低于 0℃/-20℃ 建议升级 Q355C/D/ND 并按 7.4.2.2 核对薄壁试样要求";

        var highlights = new List<string>
        {
            $"模型联动：{modelLinkageSummary}。",
            $"最小壁厚（{gb3811.StandardCode} 表34）：{wallSummary}。",
            $"宽厚比与局部稳定（{gb50017.StandardCode}）：{widthThicknessSummary}。",
            $"国标型钢与含圆角特性（GB/T 6728-2025）：{standardSectionSummary}。",
            $"焊接构造边界（GB 50661-2011）：{weldingDetailingSummary}。",
            $"材质与低温冲击（GB/T 1591-2018）：{steelMaterialSummary}。",
            $"仿真必复核红线：{simulationChecklistSummary}"
        };

        return new DesignBoundaryEvaluationResult(
            MemberRole: memberRole,
            MemberDisplayName: memberName,
            Mechanics: calc,
            CurrentWidthMm: currentMemberSpec?.WidthMm,
            CurrentHeightMm: currentMemberSpec?.HeightMm,
            CurrentThicknessMm: currentMemberSpec?.ThicknessMm,
            EpsilonK: epsilonK,
            NetFlatWidthThicknessRatio: netRatio,
            OuterWidthThicknessRatio: outerRatio,
            MinGyrationRadiusMm: rMin,
            BaselineMinGyrationRadiusMm: baseRMin,
            SectionWidthThicknessClass: sectionClass,
            ColumnCurveClass: columnCurveClass,
            ExceedsAxialLocalBucklingLimit: exceedsAxial,
            NeedsEffectiveWidthReduction: needsEffWidth,
            ExceedsTubeJointOuterRatioLimit: exceedsJointOuter,
            CorrosionWallRisk: wallRisk,
            CorrosionWallSummary: wallSummary,
            WidthThicknessSummary: widthThicknessSummary,
            ModelLinkageSummary: modelLinkageSummary,
            SimulationChecklistSummary: simulationChecklistSummary,
            Highlights: highlights,
            RoundedSection: rounded,
            StandardSectionSummary: standardSectionSummary,
            WeldingDetailingSummary: weldingDetailingSummary,
            SteelMaterialSummary: steelMaterialSummary);
    }

    public static string FormatCompactPreConfirmationBlock(IReadOnlyList<DesignBoundaryEvaluationResult> evaluations)
    {
        if (evaluations.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var eval in evaluations)
        {
            var currentTag = eval.CurrentWidthMm.HasValue && eval.CurrentHeightMm.HasValue && eval.CurrentThicknessMm.HasValue
                ? $"，当前 {eval.CurrentWidthMm.Value:0.##}×{eval.CurrentHeightMm.Value:0.##}×{eval.CurrentThicknessMm.Value:0.##} mm"
                : string.Empty;
            var riskTag = eval.HasBoundaryWarning ? "【含规范风险提示】" : "【几何与规范初步边界核对】";
            sb.AppendLine($"{riskTag}本车{eval.MemberDisplayName}目标截面 {eval.Mechanics.SpecText} mm{currentTag}：");
            foreach (var item in eval.Highlights)
            {
                sb.AppendLine($"- {item}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static decimal ComputeAllowableStressMpa(
        VehicleModelBaselineProfile vehicle,
        GbT3811BoundaryProfile gb3811,
        decimal safetyFactor)
    {
        if (safetyFactor <= 0m)
        {
            return 0m;
        }

        var yieldRatio = vehicle.TensileStrengthMinMpa > 0m
            ? vehicle.YieldStrengthMpa / vehicle.TensileStrengthMinMpa
            : 0m;
        var numerator = yieldRatio >= gb3811.HighStrengthYieldRatioThreshold
            ? 0.5m * vehicle.YieldStrengthMpa + 0.35m * vehicle.TensileStrengthMinMpa
            : vehicle.YieldStrengthMpa;
        return Math.Round(numerator / safetyFactor, 1);
    }

    private static decimal ComputeMinGyrationRadiusMm(RectangularTubeMechanicsResult calc)
    {
        if (!calc.IsGeometryValid || calc.AreaMm2 <= 0m)
        {
            return 0m;
        }

        var minInertia = Math.Min(calc.IxMm4, calc.IyMm4);
        if (minInertia <= 0m)
        {
            return 0m;
        }

        return Math.Round((decimal)Math.Sqrt((double)(minInertia / calc.AreaMm2)), 1);
    }
}
