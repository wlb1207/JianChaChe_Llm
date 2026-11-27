using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services.Design;

namespace LlmWpfPrototype.ViewModels;

public sealed partial class MainViewModel
{
    internal sealed record DiscussedChordPlan(
        DesignBoundaryMemberRole TargetRole,
        string MemberDisplayName,
        decimal WidthMm,
        decimal HeightMm,
        decimal ThicknessMm,
        bool KeepOtherChordUnchanged,
        string SuggestedCommand,
        string AlternativeFullCommand);

    private static readonly Regex ClauseSplitRegex = new(
        @"[。\n；;!！?？]+",
        RegexOptions.Compiled);

    private static readonly Regex ChordRoleTokenRegex = new(
        @"上弦杆|上弦|下弦杆|下弦|下桁架",
        RegexOptions.Compiled);

    private static readonly Regex StandaloneFullSpecRegex = new(
        @"(?<w>\d{2,3}(?:\.\d+)?)\s*[×xX*]\s*(?<h>\d{2,3}(?:\.\d+)?)\s*[×xX*]\s*(?<t>\d{1,2}(?:\.\d+)?)\s*(?:mm|毫米)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DiscussedWallOnlyRegex = new(
        @"(?<member>上弦杆|上弦|下弦杆|下弦|下桁架)[^。\n；;，,]{0,16}?(?:壁厚|减薄|薄至|厚)[^0-9。\n；;，,]{0,8}?(?<t>\d{1,2}(?:\.\d+)?)\s*(?:mm|毫米)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DiscussedOuterOnlyRegex = new(
        @"(?<member>上弦杆|上弦|下弦杆|下弦|下桁架)[^。\n；;，,]{0,16}?(?:改成|改为|调整为|改到|截面为)[^0-9。\n；;，,]{0,6}?(?<w>\d{2,3}(?:\.\d+)?)\s*[×xX*]\s*(?<h>\d{2,3}(?:\.\d+)?)(?!\s*[×xX*]\s*\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] BaselineReferencePrefixHints =
    [
        "保持", "维持", "保留", "原为", "目前", "当前", "母型", "仍为", "均为", "原来", "基准", "由当前", "从当前"
    ];

    internal DiscussedChordPlan? TryExtractRecentDiscussedPlan()
    {
        var history = _memoryStore.GetMainChatHistory();
        if (history.Count == 0)
        {
            return null;
        }

        var currentUpper = ActiveProject?.UpperChord ?? MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆");
        var currentLower = ActiveProject?.LowerChord ?? MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆");

        var startIndex = Math.Max(0, history.Count - 8);
        for (var i = history.Count - 1; i >= startIndex; i--)
        {
            var msg = history[i];
            if (string.IsNullOrWhiteSpace(msg.Content))
            {
                continue;
            }

            var text = msg.Content.Trim();
            if (text.StartsWith("工作模型已打开", StringComparison.Ordinal) ||
                text.StartsWith("模型已打开", StringComparison.Ordinal) ||
                text.StartsWith("已完成：", StringComparison.Ordinal) ||
                text.StartsWith("待确认修改：", StringComparison.Ordinal) ||
                text.Contains("修改指令示例：", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryExtractPlanFromMessageText(text, currentUpper, currentLower, out var plan))
            {
                return plan;
            }
        }

        return null;
    }

    private static bool TryExtractPlanFromMessageText(
        string text,
        MemberSectionSpecRecord currentUpper,
        MemberSectionSpecRecord currentLower,
        out DiscussedChordPlan? plan)
    {
        plan = null;
        var cleanText = text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);

        // 1) Clause-by-clause extraction for explicit W×H×T specs (handles "上弦杆由当前的70×70×5 mm改为70×70×4 mm" accurately).
        foreach (var clause in ClauseSplitRegex.Split(cleanText))
        {
            if (string.IsNullOrWhiteSpace(clause))
            {
                continue;
            }

            foreach (Match specMatch in StandaloneFullSpecRegex.Matches(clause))
            {
                var beforeSpec = clause[..specMatch.Index];
                var roleMatches = ChordRoleTokenRegex.Matches(beforeSpec);
                if (roleMatches.Count == 0)
                {
                    continue;
                }

                var nearestRoleMatch = roleMatches[^1];
                var immediatePrefix = beforeSpec[Math.Max(0, beforeSpec.Length - 10)..];
                if (IsImmediateKeepOrBaselinePrefix(immediatePrefix))
                {
                    continue;
                }

                if (!decimal.TryParse(specMatch.Groups["w"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                    !decimal.TryParse(specMatch.Groups["h"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ||
                    !decimal.TryParse(specMatch.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                {
                    continue;
                }

                var role = ParseChordRole(nearestRoleMatch.Value);
                if (role == DesignBoundaryMemberRole.Unspecified)
                {
                    continue;
                }

                var current = role == DesignBoundaryMemberRole.UpperChord ? currentUpper : currentLower;
                if (w == current.WidthMm && h == current.HeightMm && t == current.ThicknessMm)
                {
                    continue;
                }

                if (w is < 30 or > 260 || h is < 30 or > 260 || t is < 1.5m or > 25m)
                {
                    continue;
                }

                plan = CreateDiscussedChordPlan(role, w, h, t, current, cleanText);
                return true;
            }
        }

        // 2) Fallback: explicit wall-thickness target (e.g., "上弦杆减薄至 4 mm").
        foreach (Match match in DiscussedWallOnlyRegex.Matches(cleanText))
        {
            if (IsBaselineKeepClause(cleanText, match))
            {
                continue;
            }

            if (!decimal.TryParse(match.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ||
                t is < 1.5m or > 25m)
            {
                continue;
            }

            var role = ParseChordRole(match.Groups["member"].Value);
            if (role == DesignBoundaryMemberRole.Unspecified)
            {
                continue;
            }

            var current = role == DesignBoundaryMemberRole.UpperChord ? currentUpper : currentLower;
            if (t == current.ThicknessMm)
            {
                continue;
            }

            plan = CreateDiscussedChordPlan(role, current.WidthMm, current.HeightMm, t, current, cleanText);
            return true;
        }

        // 3) Fallback: explicit outer-section target (e.g., "把上弦杆改成 80x80").
        foreach (Match match in DiscussedOuterOnlyRegex.Matches(cleanText))
        {
            if (IsBaselineKeepClause(cleanText, match))
            {
                continue;
            }

            if (!decimal.TryParse(match.Groups["w"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                !decimal.TryParse(match.Groups["h"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ||
                w is < 30 or > 260 || h is < 30 or > 260)
            {
                continue;
            }

            var role = ParseChordRole(match.Groups["member"].Value);
            if (role == DesignBoundaryMemberRole.Unspecified)
            {
                continue;
            }

            var current = role == DesignBoundaryMemberRole.UpperChord ? currentUpper : currentLower;
            if (w == current.WidthMm && h == current.HeightMm)
            {
                continue;
            }

            plan = CreateDiscussedChordPlan(role, w, h, current.ThicknessMm, current, cleanText);
            return true;
        }

        return false;
    }

    private static bool IsImmediateKeepOrBaselinePrefix(string immediatePrefix)
    {
        if (string.IsNullOrWhiteSpace(immediatePrefix))
        {
            return false;
        }

        if (immediatePrefix.Contains("改为", StringComparison.Ordinal) ||
            immediatePrefix.Contains("改成", StringComparison.Ordinal) ||
            immediatePrefix.Contains("调整为", StringComparison.Ordinal) ||
            immediatePrefix.Contains("减至", StringComparison.Ordinal) ||
            immediatePrefix.Contains("减薄至", StringComparison.Ordinal) ||
            immediatePrefix.Contains("减到", StringComparison.Ordinal) ||
            immediatePrefix.Contains("调到", StringComparison.Ordinal))
        {
            return false;
        }

        return BaselineReferencePrefixHints.Any(hint =>
            immediatePrefix.Contains(hint, StringComparison.Ordinal));
    }

    private static bool IsBaselineKeepClause(string fullText, Match match)
    {
        var windowStart = Math.Max(0, match.Index - 8);
        var innerSlice = fullText.Substring(windowStart, Math.Min(fullText.Length - windowStart, match.Length + 8));
        var memberEnd = match.Groups["member"].Index + match.Groups["member"].Length;
        var betweenStart = memberEnd;
        var valueStart = match.Groups["w"].Success
            ? match.Groups["w"].Index
            : match.Groups["t"].Index;
        var betweenText = valueStart > betweenStart
            ? fullText.Substring(betweenStart, valueStart - betweenStart)
            : string.Empty;

        // If "改为/改成/调整为/减薄至" appears right before the value, it's a target even if "由当前的" appeared earlier.
        if (betweenText.Contains("改为", StringComparison.Ordinal) ||
            betweenText.Contains("改成", StringComparison.Ordinal) ||
            betweenText.Contains("调整为", StringComparison.Ordinal) ||
            betweenText.Contains("减至", StringComparison.Ordinal) ||
            betweenText.Contains("减薄至", StringComparison.Ordinal) ||
            betweenText.Contains("减到", StringComparison.Ordinal) ||
            betweenText.Contains("调到", StringComparison.Ordinal))
        {
            return false;
        }

        return BaselineReferencePrefixHints.Any(hint =>
            betweenText.Contains(hint, StringComparison.Ordinal) ||
            innerSlice.StartsWith(hint, StringComparison.Ordinal));
    }

    private static DesignBoundaryMemberRole ParseChordRole(string memberToken)
    {
        if (memberToken.Contains("上弦", StringComparison.Ordinal))
        {
            return DesignBoundaryMemberRole.UpperChord;
        }

        if (memberToken.Contains("下弦", StringComparison.Ordinal) ||
            memberToken.Contains("下桁架", StringComparison.Ordinal))
        {
            return DesignBoundaryMemberRole.LowerChord;
        }

        return DesignBoundaryMemberRole.Unspecified;
    }

    private static DiscussedChordPlan CreateDiscussedChordPlan(
        DesignBoundaryMemberRole role,
        decimal w,
        decimal h,
        decimal t,
        MemberSectionSpecRecord current,
        string fullMessageText)
    {
        var memberName = role == DesignBoundaryMemberRole.UpperChord ? "上弦杆" : "下弦杆";
        var otherHint = role == DesignBoundaryMemberRole.UpperChord ? "下弦" : "上弦";
        var keepOther = fullMessageText.Contains($"{otherHint}保持", StringComparison.Ordinal) ||
                        fullMessageText.Contains($"{otherHint}杆保持", StringComparison.Ordinal) ||
                        fullMessageText.Contains($"{otherHint}暂时保持", StringComparison.Ordinal) ||
                        fullMessageText.Contains($"{otherHint}杆暂时保持", StringComparison.Ordinal) ||
                        fullMessageText.Contains($"{otherHint}保留", StringComparison.Ordinal) ||
                        fullMessageText.Contains($"{otherHint}杆保留", StringComparison.Ordinal) ||
                        fullMessageText.Contains($"{otherHint}先不动", StringComparison.Ordinal);

        var wallOnly = w == current.WidthMm && h == current.HeightMm && t != current.ThicknessMm;
        var outerOnly = (w != current.WidthMm || h != current.HeightMm) && t == current.ThicknessMm;
        var fullCommand = $"把{memberName}截面改成 {w:0.##}x{h:0.##}x{t:0.##}";
        var suggestedCommand = wallOnly
            ? $"把{memberName}壁厚改成 {t:0.##}"
            : outerOnly
                ? $"把{memberName}截面改成 {w:0.##}x{h:0.##}"
                : fullCommand;

        return new DiscussedChordPlan(
            role,
            memberName,
            w,
            h,
            t,
            keepOther,
            suggestedCommand,
            fullCommand);
    }

    private string BuildContextualModelOpenedReply()
    {
        var upper = ActiveProject?.UpperChord ?? MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆");
        var lower = ActiveProject?.LowerChord ?? MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆");
        var upperSpec = upper.SpecText;
        var lowerSpec = lower.SpecText;
        var discussedPlan = TryExtractRecentDiscussedPlan();

        if (discussedPlan is not null)
        {
            var targetSpec = $"{discussedPlan.WidthMm:0.##}×{discussedPlan.HeightMm:0.##}×{discussedPlan.ThicknessMm:0.##} mm";
            var otherText = discussedPlan.KeepOtherChordUnchanged
                ? (discussedPlan.TargetRole == DesignBoundaryMemberRole.UpperChord
                    ? $"，下弦杆暂保持 `{lowerSpec} mm`"
                    : $"，上弦杆暂保持 `{upperSpec} mm`")
                : string.Empty;
            var altHint = !string.Equals(discussedPlan.SuggestedCommand, discussedPlan.AlternativeFullCommand, StringComparison.Ordinal)
                ? $"（或 `{discussedPlan.AlternativeFullCommand}`）"
                : string.Empty;

            return $"""
工作模型已打开（当前实时截面：上弦杆 `{upperSpec} mm`，下弦杆 `{lowerSpec} mm`）。

衔接刚才商定的方案（**{discussedPlan.MemberDisplayName} `{targetSpec}`**{otherText}）：
1. 您可以先在 SolidWorks 中核对主弦杆与腹杆、横管及折线法兰的连接壁厚是否匹配；
2. 确认无误后，直接发送 **`{discussedPlan.SuggestedCommand}`**{altHint}，我会先给出多规范边界预检并经您确认后再写入模型。
""".Trim();
        }

        return $"""
工作模型已打开（当前实时截面：上弦杆 `{upperSpec} mm`，下弦杆 `{lowerSpec} mm`）。

接下来您可以直接继续推进：
- **先讨论改型方案**：例如“围绕 7.35 m 大悬臂给我一个首轮轻量化减薄建议”；
- **直接试调弦杆规格**：支持调整**上弦杆**或**下弦杆**的外廓与壁厚（例如 `把上弦杆壁厚改成 4` 或 `把下弦杆截面改成 70x70x4`），写入前都会先弹出多规范预检并请您确认。
""".Trim();
    }

    private string BuildPendingPlanDeviationNotice(IReadOnlyList<DesignBoundaryEvaluationResult> evaluations)
    {
        if (evaluations.Count == 0)
        {
            return string.Empty;
        }

        var discussed = TryExtractRecentDiscussedPlan();
        if (discussed is null)
        {
            return string.Empty;
        }

        var matchingEval = evaluations.FirstOrDefault(item => item.MemberRole == discussed.TargetRole);
        if (matchingEval is null)
        {
            return string.Empty;
        }

        var target = matchingEval.Mechanics;
        if (target.WidthMm == discussed.WidthMm &&
            target.HeightMm == discussed.HeightMm &&
            target.ThicknessMm == discussed.ThicknessMm)
        {
            return string.Empty;
        }

        var discussedSpec = $"{discussed.WidthMm:0.##}×{discussed.HeightMm:0.##}×{discussed.ThicknessMm:0.##} mm";
        var submittedSpec = $"{target.WidthMm:0.##}×{target.HeightMm:0.##}×{target.ThicknessMm:0.##} mm";
        return $"【方案衔接提醒】前文刚商定的{discussed.MemberDisplayName}候选为 `{discussedSpec}`，而本次提交的是 `{submittedSpec}`；若是有意对比另一组方案可直接确认，若想执行原方案可先取消并发送 `{discussed.SuggestedCommand}`。";
    }

    private string BuildContextualModificationSuccessReply(string shortHeadline, LlmParseResult? parseResult)
    {
        var headline = string.IsNullOrWhiteSpace(shortHeadline) ? "已完成模型尺寸修改。" : shortHeadline.Trim();
        if (ActiveProject is not { } active)
        {
            return headline;
        }

        var upper = active.UpperChord;
        var lower = active.LowerChord;
        var upperMechanics = upper.Mechanics;
        var sb = new StringBuilder();
        sb.AppendLine($"{headline.TrimEnd('。')}（项目已更新至 **{active.VersionTag}**，SolidWorks 尺寸已重建、保存并回读核对通过）。");
        sb.AppendLine();
        sb.AppendLine($"- **当前生效截面**：上弦杆 `{upper.SpecText} mm`、下弦杆 `{lower.SpecText} mm`");
        sb.AppendLine($"- **几何联动实况**：上弦横向联动红管长度 `{upperMechanics.LinkedUpperRedTubeLengthMm:0.##} mm`，中部水平通道有效净宽 `{upperMechanics.EffectiveHorizontalWalkwayWidthMm:0.##} mm`（名义 1270 mm）");

        var discussed = TryExtractRecentDiscussedPlan();
        if (discussed is not null)
        {
            var discussedSpec = $"{discussed.WidthMm:0.##}×{discussed.HeightMm:0.##}×{discussed.ThicknessMm:0.##} mm";
            sb.AppendLine($"- **下一步建议**：您可以在 SolidWorks 中目视复核折线法兰与支管连接；若后续想切换至前文讨论的{discussed.MemberDisplayName} `{discussedSpec}` 方案，直接发送 `{discussed.AlternativeFullCommand}` 即可，也可基于当前 **{active.VersionTag}** 复核规范边界或发送 `打开 APDL` 开展仿真对比。");
        }
        else
        {
            sb.AppendLine($"- **下一步建议**：您可以在 SolidWorks 中目视复核节点连接；接着可以继续微调壁厚/下弦杆，或基于当前 **{active.VersionTag}** 方案咨询多规范复核要点、发送 `打开 APDL` 开展有限元对比校核。");
        }

        return sb.ToString().TrimEnd();
    }
}
