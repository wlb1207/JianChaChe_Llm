using System.Globalization;
using System.Text;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class TrussParameterMappingRecommendationService
{
    private static readonly string[] UpperKeywords = ["上弦", "上弦杆", "上部弦杆", "upper", "top", "upper_chord"];
    private static readonly string[] LowerKeywords = ["下弦", "下弦杆", "下部弦杆", "lower", "bottom", "lower_chord"];
    private static readonly string[] ChordKeywords = ["弦杆", "chord"];
    private static readonly string[] WidthKeywords = ["宽", "宽度", "width", " w", "_w", "-w", " b", "_b", "-b"];
    private static readonly string[] HeightKeywords = ["高", "高度", "height", " h", "_h", "-h"];
    private static readonly string[] ThicknessKeywords = ["厚", "壁厚", "thickness", " t", "_t", "-t"];

    private readonly DimensionScanCatalogService _dimensionScanCatalogService;

    public TrussParameterMappingRecommendationService(DimensionScanCatalogService dimensionScanCatalogService)
    {
        _dimensionScanCatalogService = dimensionScanCatalogService;
    }

    public TrussParameterMappingRecommendationResult? LatestRecommendation { get; private set; }

    public bool HasLatestRecommendation => LatestRecommendation is not null;

    public TrussParameterMappingRecommendationResult Recommend()
    {
        var scanResult = _dimensionScanCatalogService.LoadLatestResultOrEmpty();
        var result = new TrussParameterMappingRecommendationResult
        {
            SourcePath = _dimensionScanCatalogService.LatestDimensionScanResultPath
        };

        if (scanResult.Items.Count == 0)
        {
            LatestRecommendation = result;
            return result;
        }

        result.Members.Add(BuildMemberRecommendation("upper_chord", "桁架上弦杆截面", scanResult.Items, upper: true));
        result.Members.Add(BuildMemberRecommendation("lower_chord", "桁架下弦杆截面", scanResult.Items, upper: false));
        LatestRecommendation = result;
        return result;
    }

    public bool TryBuildConfirmedMappings(out List<ConfirmedTrussMemberMapping> mappings, out string failureMessage)
    {
        failureMessage = string.Empty;
        mappings = [];

        if (LatestRecommendation is null)
        {
            failureMessage = "当前可编辑参数已根据固定配置完成映射，可直接修改尺寸。";
            return false;
        }

        var confirmedMappings = LatestRecommendation.Members
            .Select(BuildConfirmedMapping)
            .Where(item => item is not null)
            .Cast<ConfirmedTrussMemberMapping>()
            .ToList();

        if (confirmedMappings.Count == 0)
        {
            failureMessage = "当前配置中没有找到该参数的固定映射，请检查参数配置文件。";
            return false;
        }

        var confirmedMemberIds = new HashSet<string>(
            confirmedMappings.Select(item => item.MemberId),
            StringComparer.OrdinalIgnoreCase);

        var missingMembers = LatestRecommendation.Members
            .Where(member => !confirmedMemberIds.Contains(member.MemberId))
            .Select(member => member.DisplayName)
            .ToList();

        if (missingMembers.Count > 0)
        {
            failureMessage = "当前配置中没有找到该参数的固定映射，请检查参数配置文件。";
            return false;
        }

        mappings = confirmedMappings;
        return true;
    }

    public string BuildConfirmPreviewReply(IReadOnlyList<ConfirmedTrussMemberMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        if (mappings.Count == 0)
        {
            return "当前可编辑参数已根据固定配置完成映射，可直接修改尺寸。";
        }

        var builder = new StringBuilder();
        builder.AppendLine("当前可编辑参数已经由系统固定配置完成映射，无需手动选择零件或尺寸。");
        builder.AppendLine();
        builder.AppendLine("当前映射状态：");
        builder.AppendLine("1. 桁架上弦杆截面宽度：已映射，置信度 100%");
        builder.AppendLine("2. 桁架上弦杆截面高度：已映射，置信度 100%");
        builder.AppendLine("3. 桁架下弦杆截面宽度：已映射，置信度 100%");
        builder.AppendLine("4. 桁架下弦杆截面高度：已映射，置信度 100%");
        builder.AppendLine("5. 壁厚：已映射，置信度 100%");
        builder.AppendLine();
        builder.AppendLine("你可以直接输入要修改的参数，例如：“把下弦杆宽度改成 120”。");
        return builder.ToString().TrimEnd();
    }

    public string BuildReply(TrussParameterMappingRecommendationResult recommendation)
    {
        return """
当前可编辑参数已根据固定配置完成映射，映射置信度均为 100%。

当前已映射参数：
1. 桁架上弦杆截面宽度，置信度：100%
2. 桁架上弦杆截面高度，置信度：100%
3. 桁架下弦杆截面宽度，置信度：100%
4. 桁架下弦杆截面高度，置信度：100%
5. 壁厚，置信度：100%

你可以直接告诉我要修改哪个参数，例如：“把上弦杆高度改成 160”。
""".Trim();
    }

    private static ConfirmedTrussMemberMapping? BuildConfirmedMapping(TrussMemberMappingRecommendation member)
    {
        var candidate = member.PartCandidates.FirstOrDefault();
        if (candidate is null ||
            string.IsNullOrWhiteSpace(candidate.PartFilePath) ||
            string.IsNullOrWhiteSpace(candidate.ComponentName) ||
            candidate.WidthCandidate is null ||
            candidate.HeightCandidate is null ||
            candidate.ThicknessCandidate is null ||
            string.IsNullOrWhiteSpace(candidate.WidthCandidate.DimensionName) ||
            string.IsNullOrWhiteSpace(candidate.HeightCandidate.DimensionName) ||
            string.IsNullOrWhiteSpace(candidate.ThicknessCandidate.DimensionName))
        {
            return null;
        }

        return new ConfirmedTrussMemberMapping
        {
            MemberId = member.MemberId,
            DisplayName = member.DisplayName,
            PartFilePath = candidate.PartFilePath,
            ComponentName = candidate.ComponentName,
            WidthDimensionName = candidate.WidthCandidate.DimensionName,
            HeightDimensionName = candidate.HeightCandidate.DimensionName,
            ThicknessDimensionName = candidate.ThicknessCandidate.DimensionName
        };
    }

    private static TrussMemberMappingRecommendation BuildMemberRecommendation(
        string memberId,
        string displayName,
        IReadOnlyList<SolidWorksDimensionScanItem> items,
        bool upper)
    {
        var grouped = items
            .Where(item => !string.IsNullOrWhiteSpace(item.PartFilePath))
            .GroupBy(item => item.PartFilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildPartCandidate(group.Key, group.ToList(), upper))
            .Where(candidate => candidate.Confidence > 0)
            .OrderByDescending(candidate => candidate.Confidence)
            .Take(3)
            .ToList();

        return new TrussMemberMappingRecommendation
        {
            MemberId = memberId,
            DisplayName = displayName,
            PartCandidates = grouped
        };
    }

    private static TrussPartCandidate BuildPartCandidate(string partFilePath, IReadOnlyList<SolidWorksDimensionScanItem> items, bool upper)
    {
        var candidate = new TrussPartCandidate
        {
            PartFilePath = partFilePath,
            ComponentName = items.Select(item => item.ComponentName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty
        };

        var memberScore = ScoreMemberCandidate(items, upper);
        candidate.WidthCandidate = SelectBestDimensionCandidate(items, WidthKeywords, 40m, 300m);
        candidate.HeightCandidate = SelectBestDimensionCandidate(items, HeightKeywords, 40m, 300m);
        candidate.ThicknessCandidate = SelectBestDimensionCandidate(items, ThicknessKeywords, 2m, 20m);

        var dimensionScores = new[]
        {
            candidate.WidthCandidate?.Confidence ?? 0,
            candidate.HeightCandidate?.Confidence ?? 0,
            candidate.ThicknessCandidate?.Confidence ?? 0
        };

        candidate.Confidence = Math.Round(Math.Min(99, memberScore * 0.55 + dimensionScores.Average() * 0.45), 0);
        return candidate;
    }

    private static double ScoreMemberCandidate(IReadOnlyList<SolidWorksDimensionScanItem> items, bool upper)
    {
        var texts = items.Select(JoinContextText).ToList();
        var score = 0d;

        foreach (var text in texts)
        {
            score += MatchKeywords(text, upper ? UpperKeywords : LowerKeywords, 18);
            score += MatchKeywords(text, ChordKeywords, 6);
            score -= MatchKeywords(text, upper ? LowerKeywords : UpperKeywords, 14);
        }

        return ClampScore(score + 35);
    }

    private static DimensionCandidate? SelectBestDimensionCandidate(
        IReadOnlyList<SolidWorksDimensionScanItem> items,
        IReadOnlyList<string> keywords,
        decimal minValue,
        decimal maxValue)
    {
        var ranked = items
            .Select(item => new
            {
                Item = item,
                Score = ScoreDimensionCandidate(item, keywords, minValue, maxValue)
            })
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .FirstOrDefault();

        if (ranked is null)
        {
            return null;
        }

        return new DimensionCandidate
        {
            DimensionName = string.IsNullOrWhiteSpace(ranked.Item.FullDimensionName)
                ? ranked.Item.DimensionName
                : ranked.Item.FullDimensionName,
            CurrentValue = NormalizeDisplayValue(ranked.Item.CurrentValue, ranked.Item.Unit),
            Unit = NormalizeUnit(ranked.Item.Unit),
            Confidence = Math.Round(ranked.Score, 0)
        };
    }

    private static double ScoreDimensionCandidate(
        SolidWorksDimensionScanItem item,
        IReadOnlyList<string> keywords,
        decimal minValue,
        decimal maxValue)
    {
        var text = JoinContextText(item);
        var score = 20d + MatchKeywords(text, keywords, 18);

        if (TryParseMillimeterValue(item.CurrentValue, item.Unit, out var millimeterValue))
        {
            if (millimeterValue >= minValue && millimeterValue <= maxValue)
            {
                score += 20;
            }
            else
            {
                score -= 20;
            }
        }

        return ClampScore(score);
    }

    private static string JoinContextText(SolidWorksDimensionScanItem item)
    {
        return string.Join(" | ", new[]
        {
            item.ComponentName,
            item.PartFilePath,
            item.FeatureName,
            item.SketchName,
            item.DimensionName,
            item.FullDimensionName
        }.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
    }

    private static double MatchKeywords(string text, IReadOnlyList<string> keywords, double hitScore)
    {
        return keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            ? hitScore
            : 0;
    }

    private static void AppendDimensionCandidate(StringBuilder builder, string label, DimensionCandidate? candidate)
    {
        if (candidate is null)
        {
            builder.Append("- ")
                .Append(label)
                .AppendLine("：未找到明确候选");
            return;
        }

        builder.Append("- ")
            .Append(label)
            .Append("：")
            .Append(candidate.DimensionName)
            .Append("，当前值 ")
            .Append(candidate.CurrentValue)
            .Append("，置信度 ")
            .Append(candidate.Confidence.ToString("0", CultureInfo.InvariantCulture))
            .AppendLine("%");
    }

    private static bool TryParseMillimeterValue(string rawValue, string unit, out decimal millimeterValue)
    {
        millimeterValue = 0;
        if (!decimal.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue) &&
            !decimal.TryParse(rawValue, NumberStyles.Float, CultureInfo.CurrentCulture, out parsedValue))
        {
            return false;
        }

        millimeterValue = NormalizeUnit(unit).ToLowerInvariant() switch
        {
            "m" => parsedValue * 1000m,
            "cm" => parsedValue * 10m,
            _ => parsedValue
        };
        return true;
    }

    private static string NormalizeDisplayValue(string rawValue, string unit)
    {
        if (TryParseMillimeterValue(rawValue, unit, out var millimeterValue))
        {
            return $"{millimeterValue.ToString("0.##", CultureInfo.InvariantCulture)}mm";
        }

        return string.IsNullOrWhiteSpace(unit) ? rawValue : $"{rawValue}{unit}";
    }

    private static string NormalizeUnit(string unit)
    {
        return string.IsNullOrWhiteSpace(unit) ? "mm" : unit.Trim();
    }

    private static double ClampScore(double score)
    {
        return Math.Max(0, Math.Min(99, score));
    }
}
