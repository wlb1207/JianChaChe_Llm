using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services.Design;

/// <summary>
/// Structured domain knowledge & engineering design advisor for the non-telescopic single-deck rail-mounted
/// bridge inspection vehicle. Provides 3-tier parameter ranges, locked vehicle topology constraints,
/// normative design principles (JT/T 1430-2022, GB 50017-2017, GB/T 3811-2008), and real-time section
/// mechanics comparisons so the LLM guides users with concrete engineering reasoning instead of blind edits.
/// Kept strictly free of raw verified/*.md payloads or [E#] tags so main-path disclosure guards remain 100% satisfied.
/// </summary>
public static class BridgeCarDesignKnowledgeService
{
    private static readonly Regex CandidateSpecRegex = new(
        @"(?<w>\d{2,3}(?:\.\d+)?)\s*[xX×*]\s*(?<h>\d{2,3}(?:\.\d+)?)(?:\s*[xX×*]\s*(?<t>\d{1,2}(?:\.\d+)?))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DesignGuidanceTriggerTerms =
    [
        "范围", "取值", "最大", "最小", "上限", "下限", "怎么改", "如何改", "建议", "推荐", "思路",
        "优化", "轻量化", "减重", "增刚", "刚度", "挠度", "变形", "强度", "应力", "稳定", "宽厚比",
        "干涉", "净宽", "过道", "规范", "标准", "合理", "合适", "行不行", "可以吗", "能不能", "对比", "方案"
    ];

    public static string BuildVehicleBaselineAndRangeSummary()
    {
        return """
【本车固定结构基准与几何关联规律（工程事实依据）】
- 车型与拓扑锁定参数（严禁随意改动）：单层不可伸缩轨行悬挂式桥梁检查车（U型对称空间格构桁架）；横桥向总跨度 18896 mm，整机总高 8632 mm，双门架轨距中心距 4200 mm（悬挂于 28b 工字钢轨道，严禁漂移），中部水平桁架下弦长 7991 mm，两侧斜桁架倾角 32°（两侧外伸大悬臂各约 7350 mm），两片主桁架中心距 1350 mm，水平/斜段名义通行净宽 1270 / 1200 mm。
- 材料与设计工况锁定参数：主体承重结构统一采用 Q355B 低合金高强钢（屈服强度标准值 ReH=355 MPa，弹性模量 E=2.06×10^5 MPa，密度 7850 kg/m^3）；整车额定分散活载 ≤1600 kg（中部平台 ≤800 kg、两侧端部 ≤400 kg），最大工作风速 ≤12 m/s（6级风）。
- 母型截面基准：上弦杆与下弦杆（下桁架即下弦杆结构，对外统一称“下弦杆”）母型截面尺寸均为 Q355B 闭口方管 70×70×5 mm（外宽×外高×壁厚）。
- 几何联动与仿真驱动原则（不预先写死狭隘尺寸上下限，最终可行性与定量减重效果以有限元仿真校核为准）：
  1) 只要满足空心闭口截面几何有效（内腔 b-2t>0、h-2t>0）及横管联动长度有效（L_link>0），用户可按轻量化或加强思路自由探索，并结合 ANSYS APDL 仿真结果动态调整；
  2) 上弦外宽 b 变化时，上弦横向红管拉伸长度按 L=1280+(70-b) 自动补偿以维持两片桁架中心距不变；外宽变化同步影响车内通行净宽（W_net = 1270 - (b - 70)）。
""".Trim();
    }

    public static string BuildContextualEngineeringGuidance(
        string? currentUserInput,
        DesignProjectRecord? activeProject)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildVehicleBaselineAndRangeSummary());

        var input = currentUserInput?.Trim() ?? string.Empty;
        var wantsDesignGuidance = string.IsNullOrWhiteSpace(input) ||
                                  DesignGuidanceTriggerTerms.Any(term => input.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                                  CandidateSpecRegex.IsMatch(input);

        if (!wantsDesignGuidance)
        {
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine();
        sb.AppendLine("【桥梁检查车截面改型设计思路与仿真驱动原则（回答设计咨询或评价用户尺寸时请主动结合说明）】");
        sb.AppendLine("- 严禁在未完成仿真前虚报或提前列出量化减重/刚度百分比：当前尚未接入完整有限元仿真优化结果，在引导或对比修改方向时只做定性工程分析（如减小外廓或壁厚可降低自重但需重点校核悬臂挠度与稳定性；增大外廓或壁厚可提高刚度但会增加自重并缩减通道净宽），不要在未仿真时直接宣称“可减重 XX%”或“刚度提升 XX%”；定量减重与安全裕度应待用户后续开展 ANSYS APDL 仿真校核后再确定。");
        sb.AppendLine("- 开放探索、不设狭隘死边界：不要用人为写死的窄范围提前限制用户（如不要声称只能取 60~90 mm），支持用户按轻量化或局部加强目标自由试探并结合仿真结果迭代。");
        sb.AppendLine("- 受力与传力关键特征：本车中部 4.20 m 双门架支承、两侧各 7.35 m 大悬臂；门架外侧及水平—32°斜段折线法兰处弯矩最大。斜桁架在悬臂受弯下“上弦受拉、下弦受压”，因此下弦杆减薄壁厚时须格外关注受压局部稳定与压弯整体稳定。");
        sb.AppendLine("- 相关标准参考框架：总体净空、额定载荷与通行宽度参照 JT/T 1430—2022《悬挂式桥梁检查车技术条件》；Q355B 构件截面宽厚比与受压稳定参照 GB 50017—2017《钢结构设计标准》；金属结构许用应力法安全系数及悬臂静挠度控制参照 GB/T 3811—2008《起重机设计规范》（如需精确条文原文引用，系统会自动走规范资料检索通道）。");

        // If user mentioned specific candidate dimensions in this turn, provide objective geometry & linkage checks without claiming verified FEA weight reduction.
        var matches = CandidateSpecRegex.Matches(input);
        if (matches.Count > 0)
        {
            var defaultWall = activeProject?.UpperChord.ThicknessMm ?? 5m;
            sb.AppendLine("- 本轮用户提及候选截面规格的几何与联动核对（仅供建模前参考，实际强度/挠度与最终减重效果需以仿真校核为准）：");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in matches)
            {
                if (!decimal.TryParse(match.Groups["w"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                    !decimal.TryParse(match.Groups["h"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                {
                    continue;
                }

                var t = defaultWall;
                var wallProvided = match.Groups["t"].Success &&
                                   decimal.TryParse(match.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out t);
                var key = $"{w}x{h}x{t}";
                if (!seen.Add(key))
                {
                    continue;
                }

                var calc = SectionMechanicsCalculator.CalculateRectangularTube(w, h, t);
                var wallNote = wallProvided ? "" : $"（未显式指定壁厚，暂按 {t:0.##} mm 核对）";
                if (!calc.IsGeometryValid)
                {
                    sb.AppendLine($"  * 候选 {calc.SpecText} mm{wallNote}：{calc.DomainTierLabel}，{calc.EngineeringNote}");
                }
                else
                {
                    sb.AppendLine(
                        $"  * 候选 {calc.SpecText} mm{wallNote}：{calc.DomainTierLabel}；水平段有效通行净宽约 {calc.EffectiveHorizontalWalkwayWidthMm:0.#} mm，" +
                        $"若用于上弦杆则横向联动红管自动补偿至 {calc.LinkedUpperRedTubeLengthMm:0.#} mm。提示：{calc.EngineeringNote}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }
}
