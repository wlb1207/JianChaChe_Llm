using System.Globalization;
using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class TrussMemberCommandParser
{
    private static readonly Regex FullSectionRegex = new(
        @"(?<width>\d+(?:\.\d+)?)\s*[xX×*]\s*(?<height>\d+(?:\.\d+)?)\s*[xX×*]\s*(?<thickness>\d+(?:\.\d+)?)\s*(?:mm)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SectionWidthHeightRegex = new(
        @"(?<width>\d+(?:\.\d+)?)\s*[xX×*]\s*(?<height>\d+(?:\.\d+)?)\s*(?:mm)?\s*(?![xX×*])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ThicknessRegex = new(
        @"壁厚(?:改成|改为|调整为|采用)?\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>mm)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WidthRegex = new(
        @"(?:截面)?宽度(?:改成|改为|调整为|采用)?\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>mm)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HeightRegex = new(
        @"(?:截面)?高度(?:改成|改为|调整为|采用)?\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>mm)?",
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
            sectionWidthHeight.TargetMemberIds = targetMemberIds;
            sectionWidthHeight.RawText = userInput;
            result = sectionWidthHeight;
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
            CreateExpected("把上弦杆方管改成 80*80*6", [EditableTrussMemberCatalogService.UpperChordMemberId], 80, 80, 6, true),
            CreateExpected("把桁架上弦杆壁厚改成 6mm", [EditableTrussMemberCatalogService.UpperChordMemberId], null, null, 6, false),
            CreateExpected("上弦杆采用 80×80×6 截面", [EditableTrussMemberCatalogService.UpperChordMemberId], 80, 80, 6, true)
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
        if (normalized.Contains("下弦", StringComparison.Ordinal) ||
            normalized.Contains("下部弦杆", StringComparison.Ordinal))
        {
            return [];
        }

        var mentionsUpperChord =
            normalized.Contains("上弦", StringComparison.Ordinal) ||
            normalized.Contains("上部弦杆", StringComparison.Ordinal) ||
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

    private static bool TryParseFullSection(string input, out TrussMemberCommandParseResult result)
    {
        result = new TrussMemberCommandParseResult();

        var match = FullSectionRegex.Match(input);
        if (!match.Success)
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

        var match = SectionWidthHeightRegex.Match(input);
        if (!match.Success)
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

    private static bool TryParseSingleDimension(string input, out TrussMemberCommandParseResult result)
    {
        result = new TrussMemberCommandParseResult();

        if (TryParseDimension(input, ThicknessRegex, out var thickness))
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
        var match = regex.Match(input);
        return match.Success && TryParseDecimal(match.Groups["value"].Value, out value);
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
