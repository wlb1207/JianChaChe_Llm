using System.Globalization;
using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class TrussMemberCommandParser
{
    private const string LowerTrussMemberId = EditableTrussMemberCatalogService.LowerTrussMemberId;
    private const string LowerChord10MemberId = "lower_chord_10";
    private const string LowerChord33MemberId = "lower_chord_33";
    private const string LowerChord34MemberId = "lower_chord_34";

    private static readonly Regex FullSectionRegex = new(
        @"(?<width>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*[xX×*]\s*(?<height>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*[xX×*]\s*(?<thickness>\d+(?:\.\d+)?)\s*(?:mm|毫米)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SectionWidthHeightRegex = new(
        @"(?<width>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*[xX×*]\s*(?<height>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*(?![xX×*])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ThicknessKeywordRegex = new(
        @"(?:方管壁厚|管壁厚度|薄壁厚度|薄壁厚|壁厚|厚度|管壁|板厚|薄壁)\s*(?:改成|改为|调整为|采用|设为|设置为|取|=|:|：)?\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>mm|毫米)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ThicknessAliasRegex = new(
        @"(?<![a-zA-Z])t\s*(?:=|改成|改为|调整为|采用|设为|设置为|:|：)?\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>mm|毫米)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex WidthRegex = new(
        @"(?:截面)?宽度(?:改成|改为|调整为|采用)?\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>mm)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HeightRegex = new(
        @"(?:截面)?高度(?:改成|改为|调整为|采用)?\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>mm)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SquareSectionKeywordRegex = new(
        @"(?:截面尺寸|方管截面|方管尺寸|方管规格|管子规格|外形尺寸|外轮廓尺寸|方管外尺寸|方管边长|边长|规格|截面)\s*(?:改成|改为|调整为|采用|设为|设置为|变成|取)?\s*(?<value>\d+(?:\.\d+)?)\s*(?:mm|毫米)?(?:方)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SquareSectionSuffixRegex = new(
        @"(?<value>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*方(?!管)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly EditableTrussMemberCatalogService _catalogService;

    public TrussMemberCommandParser(EditableTrussMemberCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    public bool TryParse(string? userInput, out TrussMemberCommandParseResult result)
    {
        result = new TrussMemberCommandParseResult
        {
            RawText = userInput ?? string.Empty
        };

        if (string.IsNullOrWhiteSpace(userInput))
        {
            return false;
        }

        var targetMemberIds = ResolveTargetMemberIds(userInput);
        if (targetMemberIds.Count == 0)
        {
            return false;
        }

        if (TryParseFullSection(userInput, out var fullSection))
        {
            fullSection.TargetMemberIds = targetMemberIds;
            fullSection.RawText = userInput;
            result = fullSection;
            return true;
        }

        if (TryParseSectionWidthHeight(userInput, out var sectionWidthHeight))
        {
            if (TryParseThickness(userInput, out var thickness))
            {
                sectionWidthHeight.Thickness = thickness;
                sectionWidthHeight.IsFullSectionUpdate = true;
            }

            sectionWidthHeight.TargetMemberIds = targetMemberIds;
            sectionWidthHeight.RawText = userInput;
            result = sectionWidthHeight;
            return true;
        }

        if (TryParseSquareSection(userInput, out var squareSection))
        {
            if (TryParseThickness(userInput, out var thickness))
            {
                squareSection.Thickness = thickness;
                squareSection.IsFullSectionUpdate = true;
            }

            squareSection.TargetMemberIds = targetMemberIds;
            squareSection.RawText = userInput;
            result = squareSection;
            return true;
        }

        if (TryParseSingleDimension(userInput, out var singleDimension))
        {
            singleDimension.TargetMemberIds = targetMemberIds;
            singleDimension.RawText = userInput;
            result = singleDimension;
            return true;
        }

        return false;
    }

    public IReadOnlyList<string> ValidateBuiltInExamples()
    {
        var errors = new List<string>();
        var samples = new[]
        {
            CreateExpected("把桁架上弦杆截面改成 80×80×6", [EditableTrussMemberCatalogService.UpperChordMemberId], 80, 80, 6, true),
            CreateExpected("把桁架上弦杆截面改成 80x80x6", [EditableTrussMemberCatalogService.UpperChordMemberId], 80, 80, 6, true),
            CreateExpected("把桁架上弦杆截面改成 80×80", [EditableTrussMemberCatalogService.UpperChordMemberId], 80, 80, null, false),
            CreateExpected("把桁架上弦杆截面改成 80x80，壁厚改成6", [EditableTrussMemberCatalogService.UpperChordMemberId], 80, 80, 6, true),
            CreateExpected("把桁架上弦杆截面改成 80×80，厚度改成6", [EditableTrussMemberCatalogService.UpperChordMemberId], 80, 80, 6, true),
            CreateExpected("把桁架上弦杆截面改成 80*80，t=6", [EditableTrussMemberCatalogService.UpperChordMemberId], 80, 80, 6, true),
            CreateExpected("把上弦杆方管改成 80*80*6", [EditableTrussMemberCatalogService.UpperChordMemberId], 80, 80, 6, true),
            CreateExpected("把桁架上弦杆壁厚改成 6mm", [EditableTrussMemberCatalogService.UpperChordMemberId], null, null, 6, false),
            CreateExpected("上弦杆采用 80×80×6 截面", [EditableTrussMemberCatalogService.UpperChordMemberId], 80, 80, 6, true),
            CreateExpected("把下桁架改成60×60×6", [LowerTrussMemberId], 60, 60, 6, true),
            CreateExpected("把下弦杆改成60x60x4", [LowerChord10MemberId, LowerChord33MemberId, LowerChord34MemberId], 60, 60, 4, true),
            CreateExpected("下弦杆由70方改成60方", [LowerChord10MemberId, LowerChord33MemberId, LowerChord34MemberId], 60, 60, null, false),
            CreateExpected("下弦杆10壁厚改成4", [LowerChord10MemberId], null, null, 4, false),
            CreateExpected("33号下弦杆截面改成60×60", [LowerChord33MemberId], 60, 60, null, false)
        };

        foreach (var sample in samples)
        {
            if (!TryParse(sample.RawText, out var actual))
            {
                errors.Add($"示例解析失败：{sample.RawText}");
                continue;
            }

            if (!Matches(sample, actual))
            {
                errors.Add($"示例解析结果不符合预期：{sample.RawText}");
            }
        }

        return errors;
    }

    private List<string> ResolveTargetMemberIds(string input)
    {
        var normalized = NormalizeText(input);
        if (IsDedicatedLowerTrussInput(normalized) ||
            (ContainsLowerTrussAlias(normalized) &&
             !ContainsLowerChordCollectionAlias(normalized)))
        {
            var lowerTrussMember = _catalogService.FindEnabledMemberById(LowerTrussMemberId)
                ?? _catalogService.FindByNameOrAlias(input);
            if (lowerTrussMember is not null &&
                string.Equals(lowerTrussMember.Id, LowerTrussMemberId, StringComparison.OrdinalIgnoreCase))
            {
                return [lowerTrussMember.Id];
            }
        }

        var lowerChordMemberIds = ResolveLowerChordTargetMemberIds(normalized);
        if (lowerChordMemberIds.Count > 0)
        {
            return lowerChordMemberIds;
        }

        var mentionsUpperChord =
            normalized.Contains("上弦", StringComparison.Ordinal) ||
            normalized.Contains("上桁架", StringComparison.Ordinal) ||
            normalized.Contains("上部弦杆", StringComparison.Ordinal) ||
            normalized.Contains("上部桁架", StringComparison.Ordinal) ||
            normalized.Contains("上层桁架", StringComparison.Ordinal) ||
            normalized.Contains("上弦杆方管", StringComparison.Ordinal) ||
            normalized.Contains("上弦方管", StringComparison.Ordinal);

        if (!mentionsUpperChord)
        {
            return [];
        }

        var member = _catalogService.FindEnabledMemberById(EditableTrussMemberCatalogService.UpperChordMemberId)
            ?? _catalogService.FindByNameOrAlias(input);

        return member is null ? [] : [member.Id];
    }

    private List<string> ResolveLowerChordTargetMemberIds(string normalized)
    {
        if (IsDedicatedLowerTrussInput(normalized))
        {
            return [];
        }

        if (ContainsAny(normalized, "下弦杆10", "10号下弦杆"))
        {
            return BuildExistingMemberIdList(LowerChord10MemberId);
        }

        if (ContainsAny(normalized, "下弦杆33", "33号下弦杆"))
        {
            return BuildExistingMemberIdList(LowerChord33MemberId);
        }

        if (ContainsAny(normalized, "下弦杆34", "34号下弦杆"))
        {
            return BuildExistingMemberIdList(LowerChord34MemberId);
        }

        var mentionsLowerChord =
            normalized.Contains("下弦杆", StringComparison.Ordinal) ||
            normalized.Contains("下部弦杆", StringComparison.Ordinal) ||
            normalized.Contains("下部桁架弦杆", StringComparison.Ordinal) ||
            normalized.Contains("全部下弦杆", StringComparison.Ordinal) ||
            normalized.Contains("三根下弦杆", StringComparison.Ordinal);

        return mentionsLowerChord
            ? BuildExistingMemberIdList(LowerChord10MemberId, LowerChord33MemberId, LowerChord34MemberId)
            : [];
    }

    private bool ContainsLowerChordCollectionAlias(string normalized)
    {
        var lowerChordMember = _catalogService.FindEnabledMemberById(EditableTrussMemberCatalogService.LowerChordMemberId);
        if (lowerChordMember is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(lowerChordMember.Name) &&
            normalized.Contains(NormalizeText(lowerChordMember.Name), StringComparison.Ordinal))
        {
            return true;
        }

        return lowerChordMember.Aliases.Any(alias =>
            !string.IsNullOrWhiteSpace(alias) &&
            normalized.Contains(NormalizeText(alias), StringComparison.Ordinal));
    }

    private bool ContainsLowerTrussAlias(string normalized)
    {
        var lowerTrussMember = _catalogService.FindEnabledMemberById(LowerTrussMemberId);
        if (lowerTrussMember is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(lowerTrussMember.Name) &&
            normalized.Contains(NormalizeText(lowerTrussMember.Name), StringComparison.Ordinal))
        {
            return true;
        }

        return lowerTrussMember.Aliases.Any(alias =>
            !string.IsNullOrWhiteSpace(alias) &&
            normalized.Contains(NormalizeText(alias), StringComparison.Ordinal));
    }

    private static bool IsDedicatedLowerTrussInput(string normalized)
    {
        var mentionsLowerTruss =
            normalized.Contains("下桁架", StringComparison.Ordinal) ||
            normalized.Contains("下部桁架", StringComparison.Ordinal) ||
            normalized.Contains("下层桁架", StringComparison.Ordinal);

        if (!mentionsLowerTruss)
        {
            return false;
        }

        return !normalized.Contains("下弦杆", StringComparison.Ordinal) &&
               !normalized.Contains("下弦", StringComparison.Ordinal) &&
               !normalized.Contains("底弦", StringComparison.Ordinal) &&
               !normalized.Contains("桁架下弦杆", StringComparison.Ordinal) &&
               !normalized.Contains("下部弦杆", StringComparison.Ordinal) &&
               !normalized.Contains("全部下弦杆", StringComparison.Ordinal) &&
               !normalized.Contains("三根下弦杆", StringComparison.Ordinal);
    }

    private static bool TryParseFullSection(string input, out TrussMemberCommandParseResult result)
    {
        result = new TrussMemberCommandParseResult();

        if (!TryGetLastMatch(FullSectionRegex, input, out var match))
        {
            return false;
        }

        if (!TryParseDecimal(match.Groups["width"].Value, out var width) ||
            !TryParseDecimal(match.Groups["height"].Value, out var height) ||
            !TryParseDecimal(match.Groups["thickness"].Value, out var thickness))
        {
            return false;
        }

        result = new TrussMemberCommandParseResult
        {
            IsMatched = true,
            Width = width,
            Height = height,
            Thickness = thickness,
            Unit = "mm",
            IsFullSectionUpdate = true
        };
        return true;
    }

    private static bool TryParseSectionWidthHeight(string input, out TrussMemberCommandParseResult result)
    {
        result = new TrussMemberCommandParseResult();

        if (!TryGetLastMatch(SectionWidthHeightRegex, input, out var match))
        {
            return false;
        }

        if (!TryParseDecimal(match.Groups["width"].Value, out var width) ||
            !TryParseDecimal(match.Groups["height"].Value, out var height))
        {
            return false;
        }

        result = new TrussMemberCommandParseResult
        {
            IsMatched = true,
            Width = width,
            Height = height,
            Unit = "mm",
            IsFullSectionUpdate = false
        };
        return true;
    }

    private static bool TryParseSquareSection(string input, out TrussMemberCommandParseResult result)
    {
        result = new TrussMemberCommandParseResult();

        if (!TryParseDimension(input, SquareSectionKeywordRegex, out var sectionSize) &&
            !TryParseDimension(input, SquareSectionSuffixRegex, out sectionSize))
        {
            return false;
        }

        result = new TrussMemberCommandParseResult
        {
            IsMatched = true,
            Width = sectionSize,
            Height = sectionSize,
            Unit = "mm",
            IsFullSectionUpdate = false
        };
        return true;
    }

    private static bool TryParseSingleDimension(string input, out TrussMemberCommandParseResult result)
    {
        result = new TrussMemberCommandParseResult();

        if (TryParseThickness(input, out var thickness))
        {
            result = new TrussMemberCommandParseResult
            {
                IsMatched = true,
                Thickness = thickness,
                Unit = "mm",
                IsFullSectionUpdate = false
            };
            return true;
        }

        if (TryParseDimension(input, WidthRegex, out var width))
        {
            result = new TrussMemberCommandParseResult
            {
                IsMatched = true,
                Width = width,
                Unit = "mm",
                IsFullSectionUpdate = false
            };
            return true;
        }

        if (TryParseDimension(input, HeightRegex, out var height))
        {
            result = new TrussMemberCommandParseResult
            {
                IsMatched = true,
                Height = height,
                Unit = "mm",
                IsFullSectionUpdate = false
            };
            return true;
        }

        return false;
    }

    private static bool TryParseDimension(string input, Regex regex, out decimal value)
    {
        value = 0;
        if (!TryGetLastMatch(regex, input, out var match))
        {
            return false;
        }

        return match.Success && TryParseDecimal(match.Groups["value"].Value, out value);
    }

    private static bool TryGetLastMatch(Regex regex, string input, out Match match)
    {
        match = Match.Empty;
        var matches = regex.Matches(input);
        if (matches.Count == 0)
        {
            return false;
        }

        match = matches[^1];
        return match.Success;
    }

    private static bool TryParseThickness(string input, out decimal value)
    {
        if (TryParseDimension(input, ThicknessKeywordRegex, out value))
        {
            return true;
        }

        return TryParseDimension(input, ThicknessAliasRegex, out value);
    }

    private static bool TryParseDecimal(string value, out decimal parsed)
    {
        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
               decimal.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed);
    }

    private static string NormalizeText(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private List<string> BuildExistingMemberIdList(params string[] memberIds)
    {
        var results = new List<string>();
        foreach (var memberId in memberIds)
        {
            if (_catalogService.FindEnabledMemberById(memberId) is not null)
            {
                results.Add(memberId);
            }
        }

        return results;
    }

    private static bool ContainsAny(string input, params string[] values)
    {
        foreach (var value in values)
        {
            if (input.Contains(value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static TrussMemberCommandParseResult CreateExpected(
        string rawText,
        List<string> targetMemberIds,
        decimal? width,
        decimal? height,
        decimal? thickness,
        bool isFullSectionUpdate)
    {
        return new TrussMemberCommandParseResult
        {
            RawText = rawText,
            TargetMemberIds = targetMemberIds,
            Width = width,
            Height = height,
            Thickness = thickness,
            Unit = "mm",
            IsFullSectionUpdate = isFullSectionUpdate,
            IsMatched = true
        };
    }

    private static bool Matches(TrussMemberCommandParseResult expected, TrussMemberCommandParseResult actual)
    {
        return expected.IsFullSectionUpdate == actual.IsFullSectionUpdate &&
               expected.Width == actual.Width &&
               expected.Height == actual.Height &&
               expected.Thickness == actual.Thickness &&
               expected.TargetMemberIds.Count == actual.TargetMemberIds.Count &&
               !expected.TargetMemberIds.Except(actual.TargetMemberIds, StringComparer.OrdinalIgnoreCase).Any();
    }
}
