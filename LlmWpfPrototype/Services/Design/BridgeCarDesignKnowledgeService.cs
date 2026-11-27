using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services.Design;

/// <summary>
/// Model-anchored domain knowledge & multi-standard engineering design advisor for the user's
/// non-telescopic single-deck rail-mounted bridge inspection vehicle.
/// Loads externalized vehicle & three-standard boundaries from Config/design-boundary-profile.json
/// and evaluates BOTH the active SolidWorks/project model state and any candidate dimensions discussed
/// in conversation so design guidance is grounded in concrete standard clauses without detaching from
/// the user's actual model.
/// Kept strictly free of raw verified/*.md payloads or [E#] tags so disclosure guards remain 100% satisfied.
/// </summary>
public static class BridgeCarDesignKnowledgeService
{
    private static readonly Regex CandidateSpecRegex = new(
        @"(?<w>\d{2,3}(?:\.\d+)?)\s*[xX×*]\s*(?<h>\d{2,3}(?:\.\d+)?)(?:\s*[xX×*]\s*(?<t>\d{1,2}(?:\.\d+)?))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StandaloneWallThicknessRegex = new(
        @"(?:壁厚|厚度|板厚|管壁)[^0-9\r\n]{0,8}(?<t>\d{1,2}(?:\.\d+)?)\s*(?:mm|毫米)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DesignGuidanceTriggerTerms =
    [
        "范围", "取值", "最大", "最小", "上限", "下限", "怎么改", "如何改", "建议", "推荐", "思路",
        "优化", "轻量化", "减重", "增刚", "刚度", "挠度", "变形", "强度", "应力", "稳定", "宽厚比",
        "长细比", "壁厚", "厚度", "干涉", "净宽", "过道", "规范", "标准", "依据", "合理", "合适",
        "行不行", "可以吗", "能不能", "怎么样", "对比", "方案", "上弦杆", "下弦杆", "下桁架"
    ];

    public static string BuildVehicleBaselineAndRangeSummary()
    {
        var profile = DesignBoundaryProfileLoader.Load();
        var v = profile.Vehicle;
        var g3811 = profile.GbT3811;
        var g50017 = profile.Gb50017;
        var jtt = profile.JtT1430;

        var cantileverLimitJtt = Math.Round(v.CantileverLengthMm / jtt.SteelCantileverDeflectionDivisor, 1);
        var cantileverLimit3811 = Math.Round(v.CantileverLengthMm / g3811.CantileverDeflectionDivisor, 1);
        var midspanLimitJtt = Math.Round(v.GantryRailCenterSpacingMm / jtt.SteelMidspanDeflectionDivisor, 1);
        var comboA = DesignBoundaryEvaluationService.ComputeAllowableStressMpa(v, g3811, g3811.SafetyFactorComboA);
        var comboB = DesignBoundaryEvaluationService.ComputeAllowableStressMpa(v, g3811, g3811.SafetyFactorComboB);
        var comboC = DesignBoundaryEvaluationService.ComputeAllowableStressMpa(v, g3811, g3811.SafetyFactorComboC);
        var epsK = Math.Round((decimal)Math.Sqrt((double)(g50017.EpsilonKReferenceFyMpa / Math.Max(1m, v.YieldStrengthMpa))), 3);
        var axialRatioLimit = Math.Round(g50017.AxialCompressionLocalStabilityFactor * epsK, 1);

        return $"""
【本车固定结构基准、实体联动规律与三规范核心边界（来自外置配置 design-boundary-profile.json，回答设计问题时必须紧扣本车模型与以下条款依据）】
- 车型与拓扑锁定参数（严禁脱离本车实体模型泛泛而谈）：{v.VehicleName}（按 {jtt.StandardCode} 对应标准型号 `{v.StandardModelCode}`：{v.StandardModelDescription}；{v.TopologySummary}）；横桥向总跨度 {v.TotalSpanMm:0.#} mm，整机总高 {v.TotalHeightMm:0.#} mm，双门架轨距中心距 {v.GantryRailCenterSpacingMm:0.#} mm（严禁漂移），中部水平桁架下弦长 {v.HorizontalLowerChordLengthMm:0.#} mm，两侧斜桁架倾角 {v.InclinedTrussAngleDeg:0.#}°（两侧外伸大悬臂各约 {v.CantileverLengthMm:0.#} mm），两片主桁架中心距 {v.MainTrussCenterSpacingMm:0.#} mm，水平/斜段名义通行净宽 {v.NominalHorizontalWalkwayWidthMm:0.#} / {v.NominalInclinedWalkwayWidthMm:0.#} mm。
- 材料、载荷与工况基准：主体承重结构采用 {v.MaterialGrade} 低合金高强钢（屈服强度 ReH={v.YieldStrengthMpa:0.#} MPa，抗拉强度 Rm≥{v.TensileStrengthMinMpa:0.#} MPa，弹性模量 E={v.ElasticModulusMpa / 100000m:0.##}×10^5 MPa，密度 {v.DensityKgPerM3:0.#} kg/m^3，εk≈{epsK:0.000}）；整车额定分散活载 ≤{v.RatedDistributedLiveLoadKg:0.#} kg（中部 ≤{v.MiddlePlatformMaxLoadKg:0.#} kg、两端各 ≤{v.EachEndMaxLoadKg:0.#} kg；满足 {jtt.StandardCode} 5.1.3 全车 ≥{jtt.MinTotalRatedLoadKn:0.#} kN，局部集中载荷核对 ≥{jtt.MinConcentratedLoadKn:0.#} kN）；工作抗风按 6 级风口径控制（原车设计资料取 6 级风代表风速 {v.DesignMaxWorkingWindSpeedMps:0.#} m/s，{jtt.StandardCode} 5.1.4 按 6 级风上限要求 ≥{jtt.MinWorkingWindSpeedMps:0.#} m/s；二者同属 6 级风口径，后续有限元抗风工况统一按 {jtt.MinWorkingWindSpeedMps:0.#} m/s 包络校核即可，不视为先天不合规冲突）。
- 母型截面与构件分工：上弦杆与下弦杆（下桁架即下弦杆结构，对外统一称“下弦杆”）母型均为 {v.MaterialGrade} 闭口方管 {v.BaselineWidthMm:0.#}×{v.BaselineHeightMm:0.#}×{v.BaselineThicknessMm:0.#} mm（参数化模板可重建范围：外廓 {v.TemplateMinOuterMm:0.#}~{v.TemplateMaxOuterMm:0.#} mm、壁厚 {v.TemplateMinWallMm:0.#}~{v.TemplateMaxWallMm:0.#} mm）。
  1) 上弦杆模型角色：{v.UpperChordModelNote}
  2) 下弦杆模型角色：{v.LowerChordModelNote}
- 绑定本车的三本规范量化边界（作为设计依据与仿真红线，不提前写死狭隘死范围，最终定量减重与合格判定以有限元仿真校核为准）：
  1) {jtt.StandardCode}：表2 桁架净宽 {jtt.MinWalkwayClearWidthMm:0.#}~{jtt.MaxWalkwayClearWidthMm:0.#} mm、净高 {jtt.MinWalkwayClearHeightMm:0.#}~{jtt.MaxWalkwayClearHeightMm:0.#} mm；表3 钢桁架悬臂端静挠度 f'≤L'/{jtt.SteelCantileverDeflectionDivisor}（按本车 {v.CantileverLengthMm:0.#} mm 悬臂折算 ≤{cantileverLimitJtt:0.#} mm，严于 {g3811.StandardCode} L₁/{g3811.CantileverDeflectionDivisor}≈{cantileverLimit3811:0.#} mm）、跨中 f≤L/{jtt.SteelMidspanDeflectionDivisor}（≤{midspanLimitJtt:0.#} mm）；5.3.1.3 钢桁架须重防腐；6.2.2 护栏 ≥{jtt.MinGuardrailHeightMm:0.#} mm、踢脚板 ≥{jtt.MinToeboardHeightMm:0.#} mm；附录A 含 {jtt.DynamicTestLoadFactor:0.##} 倍动载与 {jtt.StaticCapacityTestLoadFactor:0.##} 倍静载无残余变形要求。
  2) {g3811.StandardCode}：表34 闭口截面防腐蚀最小壁厚（微弱 {g3811.ClosedSectionMinWallMildMm:0.#} mm / 中等 {g3811.ClosedSectionMinWallModerateMm:0.#} mm / 严重 {g3811.ClosedSectionMinWallSevereMm:0.#} mm）；5.9.2.2 板厚 <{g3811.ThinPlateWeldThresholdMm:0.#} mm 时角焊缝焊脚等于板厚；表28 主桁架弦杆容许长细比受压 ≤{g3811.ChordCompressionMaxSlenderness}、受拉 ≤{g3811.ChordTensionMaxSlenderness}；表22/式28 {v.MaterialGrade} 静强度许用应力保守值：组合A（n={g3811.SafetyFactorComboA:0.##}）≈{comboA:0.#} MPa、组合B（n={g3811.SafetyFactorComboB:0.##}）≈{comboB:0.#} MPa、组合C（n={g3811.SafetyFactorComboC:0.##}）≈{comboC:0.#} MPa。
  3) {g50017.StandardCode}：表3.5.1/7.3.1 {v.MaterialGrade} 闭口方管轴压局部不失稳净宽厚比 b₀/t≤40εk（≈{axialRatioLimit:0.#}，压弯 S1~S4 为 30/35/40/45εk）；13.1.2/13.2.1 矩形管相贯节点外缘 b/t≤40εk（≈{axialRatioLimit:0.#}）且主管外尺寸与壁厚不应小于相连腹杆/横管支管。
""".Trim();
    }

    public static string BuildContextualEngineeringGuidance(
        string? currentUserInput,
        DesignProjectRecord? activeProject)
    {
        var profile = DesignBoundaryProfileLoader.Load();
        var sb = new StringBuilder();
        sb.AppendLine(BuildVehicleBaselineAndRangeSummary());

        var input = currentUserInput?.Trim() ?? string.Empty;
        var wantsDesignGuidance = string.IsNullOrWhiteSpace(input) ||
                                  DesignGuidanceTriggerTerms.Any(term => input.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                                  CandidateSpecRegex.IsMatch(input) ||
                                  StandaloneWallThicknessRegex.IsMatch(input);

        if (!wantsDesignGuidance)
        {
            return sb.ToString().TrimEnd();
        }

        var currentUpper = activeProject?.UpperChord ?? MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆");
        var currentLower = activeProject?.LowerChord ?? MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆");

        sb.AppendLine();
        sb.AppendLine("【当前模型实时截面对应的多规范边界核对（基于你当前项目的真实模型尺寸，不要脱离当前模型作答）】");
        AppendCompactCurrentMemberCheck(sb, currentUpper, DesignBoundaryMemberRole.UpperChord, profile);
        AppendCompactCurrentMemberCheck(sb, currentLower, DesignBoundaryMemberRole.LowerChord, profile);

        sb.AppendLine();
        sb.AppendLine("【桥梁检查车截面改型设计思路与仿真驱动原则（回答设计咨询或评价用户尺寸时请主动结合本车模型与上述规范条款说明）】");
        sb.AppendLine("- 严禁脱离模型或虚报未仿真定量结果：所有建议必须结合当前项目真实的上/下弦杆尺寸、本车 7.35 m 大悬臂受力特征（斜桁架上弦受拉、下弦受压）、几何联动（红管补偿、通道净宽、法兰与主支管匹配）及上述具体规范条款来说明；在尚未完成有限元仿真前，只做定性工程趋势与规范边界核对，严禁提前编造或直接宣称“可减重 XX%”或“刚度提升 XX%”，定量减重与最终合格判定以 ANSYS APDL 有限元仿真校核为准。");
        sb.AppendLine("- 开放探索、提示而非死锁：不要用人为写死的窄范围提前限制用户（如不要声称只能取 60~90 mm）；当候选方案触及某条规范风险（如壁厚 <4 mm 低于 GB/T 3811 表34 中等腐蚀限值，或 b₀/t 逼近 GB 50017 40εk）时，应明确指出对应条款与风险，支持用户结合仿真结果迭代权衡。");

        var inferredRole = InferMemberRoleFromInput(input);
        var defaultMemberForInput = inferredRole == DesignBoundaryMemberRole.LowerChord ? currentLower : currentUpper;

        // 1) Explicit b×h×t or b×h candidates mentioned in this turn
        var matches = CandidateSpecRegex.Matches(input);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (matches.Count > 0)
        {
            sb.AppendLine("- 本轮用户提及候选截面规格的本车模型联动与三规范边界预检（供决策与改模前参考，实际强度/挠度与最终减重效果需以有限元仿真校核为准）：");
            foreach (Match match in matches)
            {
                if (!decimal.TryParse(match.Groups["w"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                    !decimal.TryParse(match.Groups["h"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                {
                    continue;
                }

                var t = defaultMemberForInput.ThicknessMm;
                var wallProvided = match.Groups["t"].Success &&
                                   decimal.TryParse(match.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out t);
                var key = $"{inferredRole}:{w}x{h}x{t}";
                if (!seen.Add(key))
                {
                    continue;
                }

                var eval = DesignBoundaryEvaluationService.EvaluateMemberSection(
                    w, h, t, inferredRole, defaultMemberForInput, profile);
                var wallNote = wallProvided ? string.Empty : $"（未显式指定壁厚，按当前模型{defaultMemberForInput.MemberName}实时壁厚 {t:0.##} mm 核对）";
                AppendCandidateEvaluationLines(sb, eval, wallNote);
            }
        }
        else
        {
            // 2) Standalone wall-thickness consultation (e.g. "下弦杆壁厚减到 3 mm 行不行？") anchored to current model b×h!
            var wallMatches = StandaloneWallThicknessRegex.Matches(input);
            if (wallMatches.Count > 0)
            {
                var targetMembers = inferredRole switch
                {
                    DesignBoundaryMemberRole.UpperChord => new[] { (currentUpper, DesignBoundaryMemberRole.UpperChord) },
                    DesignBoundaryMemberRole.LowerChord => new[] { (currentLower, DesignBoundaryMemberRole.LowerChord) },
                    _ => currentUpper.WidthMm == currentLower.WidthMm && currentUpper.HeightMm == currentLower.HeightMm
                        ? new[] { (currentUpper, DesignBoundaryMemberRole.Unspecified) }
                        : new[]
                        {
                            (currentUpper, DesignBoundaryMemberRole.UpperChord),
                            (currentLower, DesignBoundaryMemberRole.LowerChord)
                        }
                };

                var headerPrinted = false;
                foreach (Match wallMatch in wallMatches)
                {
                    if (!decimal.TryParse(wallMatch.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wallMm) ||
                        wallMm <= 0m || wallMm > 50m)
                    {
                        continue;
                    }

                    foreach (var (member, role) in targetMembers)
                    {
                        var key = $"{role}:{member.WidthMm}x{member.HeightMm}x{wallMm}";
                        if (!seen.Add(key))
                        {
                            continue;
                        }

                        if (!headerPrinted)
                        {
                            sb.AppendLine("- 本轮用户提及候选壁厚的本车模型联动与三规范边界预检（已结合当前模型实时外廓尺寸核算，实际应力/挠度以有限元仿真校核为准）：");
                            headerPrinted = true;
                        }

                        var eval = DesignBoundaryEvaluationService.EvaluateMemberSection(
                            member.WidthMm, member.HeightMm, wallMm, role, member, profile);
                        AppendCandidateEvaluationLines(sb, eval, $"（基于当前模型外廓 {member.WidthMm:0.##}×{member.HeightMm:0.##} mm 仅调整壁厚）");
                    }
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendCompactCurrentMemberCheck(
        StringBuilder sb,
        MemberSectionSpecRecord member,
        DesignBoundaryMemberRole role,
        DesignBoundaryProfile profile)
    {
        var eval = DesignBoundaryEvaluationService.EvaluateMemberSection(
            member.WidthMm, member.HeightMm, member.ThicknessMm, role, member, profile);
        if (!eval.Mechanics.IsGeometryValid)
        {
            sb.AppendLine($"- 当前{eval.MemberDisplayName}（{member.SpecText} mm）：{eval.ModelLinkageSummary}");
            return;
        }

        var wallFlag = eval.CorrosionWallRisk == DesignBoundaryRiskLevel.Warning ? "⚠️ 低于表34中等腐蚀4mm" : "满足表34中等腐蚀≥4mm";
        var ratioFlag = eval.ExceedsAxialLocalBucklingLimit || eval.ExceedsTubeJointOuterRatioLimit
            ? $"⚠️ b₀/t={eval.NetFlatWidthThicknessRatio:0.#} / b/t={eval.OuterWidthThicknessRatio:0.#} 超出局部稳定或节点限值"
            : $"b₀/t={eval.NetFlatWidthThicknessRatio:0.#}（{eval.SectionWidthThicknessClass}）、b/t={eval.OuterWidthThicknessRatio:0.#}（满足节点限值）";
        sb.AppendLine(
            $"- 当前{eval.MemberDisplayName}（{member.SpecText} mm，来源：{member.Source}）：{wallFlag}；{ratioFlag}；最小回转半径 r_min={eval.MinGyrationRadiusMm:0.0} mm。");
    }

    private static void AppendCandidateEvaluationLines(
        StringBuilder sb,
        DesignBoundaryEvaluationResult eval,
        string qualifierNote)
    {
        var riskPrefix = eval.HasBoundaryWarning ? "【需重点关注规范风险】" : "【初步边界正常】";
        sb.AppendLine($"  * {riskPrefix}候选{eval.MemberDisplayName} {eval.Mechanics.SpecText} mm{qualifierNote}（{eval.Mechanics.DomainTierLabel}）：");
        foreach (var line in eval.Highlights)
        {
            sb.AppendLine($"    - {line}");
        }
    }

    private static DesignBoundaryMemberRole InferMemberRoleFromInput(string input)
    {
        var mentionsUpper = input.Contains("上弦", StringComparison.OrdinalIgnoreCase);
        var mentionsLower = input.Contains("下弦", StringComparison.OrdinalIgnoreCase) ||
                            input.Contains("下桁架", StringComparison.OrdinalIgnoreCase);

        if (mentionsUpper && !mentionsLower)
        {
            return DesignBoundaryMemberRole.UpperChord;
        }

        if (mentionsLower && !mentionsUpper)
        {
            return DesignBoundaryMemberRole.LowerChord;
        }

        return DesignBoundaryMemberRole.Unspecified;
    }
}
