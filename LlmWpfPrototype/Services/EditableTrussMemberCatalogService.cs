using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class EditableTrussMemberCatalogService
{
    public const string UpperChordMemberId = "upper_chord";
    public const string LowerChordMemberId = "lower_chord";

    private readonly string _configPath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private EditableTrussMemberOptions? _options;

    public EditableTrussMemberCatalogService()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "Config", "editable-truss-members.json");
    }

    public bool ConfigExists()
    {
        return File.Exists(_configPath);
    }

    public string GetConfigPath()
    {
        return _configPath;
    }

    public EditableTrussMemberOptions LoadOptions()
    {
        if (_options is not null)
        {
            return _options;
        }

        if (!File.Exists(_configPath))
        {
            throw new FileNotFoundException($"缺少可编辑桁架参数配置文件：{_configPath}");
        }

        var json = File.ReadAllText(_configPath);
        _options = JsonSerializer.Deserialize<EditableTrussMemberOptions>(json, _serializerOptions)
            ?? new EditableTrussMemberOptions();
        return _options;
    }

    public void Reload()
    {
        _options = null;
    }

    public IReadOnlyList<EditableTrussMemberItem> GetAllMembers()
    {
        return LoadOptions().EditableTrussMembers;
    }

    public IReadOnlyList<EditableTrussMemberItem> GetEnabledMembers()
    {
        return GetAllMembers()
            .Where(item => item.Enabled)
            .ToList();
    }

    public EditableTrussMemberItem? FindByNameOrAlias(string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return null;
        }

        var normalizedInput = NormalizeText(userInput);
        return GetEnabledMembers().FirstOrDefault(item =>
            NormalizeText(item.Name) == normalizedInput ||
            normalizedInput.Contains(NormalizeText(item.Name), StringComparison.Ordinal) ||
            item.Aliases.Any(alias =>
                NormalizeText(alias) == normalizedInput ||
                normalizedInput.Contains(NormalizeText(alias), StringComparison.Ordinal)));
    }

    public EditableTrussMemberItem? FindEnabledMemberById(string? memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
        {
            return null;
        }

        var effectiveId = NormalizeLegacyMemberId(memberId);
        return GetEnabledMembers().FirstOrDefault(item =>
            string.Equals(item.Id, effectiveId, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasAnySolidWorksMapping(EditableTrussMemberItem member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return HasLegacyAnyDimensionMapping(member) ||
               member.PartMappings.Any(HasAnyPartMappingDimension);
    }

    public bool CanExecuteFullSectionUpdate(EditableTrussMemberItem member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return HasLegacySectionMapping(member) ||
               member.PartMappings.Any(HasValidSectionPartMapping);
    }

    public bool CanExecuteWidthUpdate(EditableTrussMemberItem member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return HasLegacyWidthMapping(member) ||
               member.PartMappings.Any(HasValidWidthPartMapping);
    }

    public bool CanExecuteHeightUpdate(EditableTrussMemberItem member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return HasLegacyHeightMapping(member) ||
               member.PartMappings.Any(HasValidHeightPartMapping);
    }

    public bool CanExecuteThicknessUpdate(EditableTrussMemberItem member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return HasLegacyThicknessMapping(member) ||
               member.PartMappings.Any(HasValidThicknessPartMapping);
    }

    public bool IsProfileDrivenWeldment(EditableTrussMemberItem member)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (string.IsNullOrWhiteSpace(member.SectionType))
        {
            return false;
        }

        return member.SectionType.Contains("weldment", StringComparison.OrdinalIgnoreCase) ||
               member.SectionType.Contains("profile", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> GetRequiredMappingFields(EditableTrussMemberItem member, TrussMemberCommandParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(parseResult);

        var requiredFields = new List<string>();
        var requiresSectionMapping = parseResult.IsFullSectionUpdate ||
                                     (parseResult.Width.HasValue && parseResult.Height.HasValue);

        if (requiresSectionMapping &&
            !HasLegacySectionMapping(member) &&
            !member.PartMappings.Any(HasValidSectionPartMapping))
        {
            requiredFields.Add("PartFilePath");
            requiredFields.Add("WidthDimensionName");
            requiredFields.Add("HeightDimensionName");
        }
        else
        {
            if (parseResult.Width.HasValue &&
                !requiresSectionMapping &&
                !HasLegacyWidthMapping(member) &&
                !member.PartMappings.Any(HasValidWidthPartMapping))
            {
                requiredFields.Add("PartFilePath");
                requiredFields.Add("WidthDimensionName");
            }

            if (parseResult.Height.HasValue &&
                !requiresSectionMapping &&
                !HasLegacyHeightMapping(member) &&
                !member.PartMappings.Any(HasValidHeightPartMapping))
            {
                AddIfMissing(requiredFields, "PartFilePath", "configured");
                requiredFields.Add("HeightDimensionName");
            }
        }

        if (parseResult.Thickness.HasValue &&
            !HasLegacyThicknessMapping(member) &&
            !member.PartMappings.Any(HasValidThicknessPartMapping))
        {
            AddIfMissing(requiredFields, "PartFilePath", "configured");
            requiredFields.Add("ThicknessDimensionName");
        }

        return requiredFields;
    }

    public IReadOnlyList<string> GetPartMappingDiagnostics(EditableTrussMemberItem member, TrussMemberCommandParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(parseResult);

        var diagnostics = new List<string>();
        if (member.PartMappings.Count == 0)
        {
            diagnostics.Add("PartMappings 未配置。");
            return diagnostics;
        }

        var requiresSectionMapping = parseResult.IsFullSectionUpdate ||
                                     (parseResult.Width.HasValue && parseResult.Height.HasValue);

        for (var index = 0; index < member.PartMappings.Count; index++)
        {
            var mapping = member.PartMappings[index];
            var issues = new List<string>();

            if (!HasConfiguredPartPath(mapping))
            {
                issues.Add("缺少 RelativePartPath/PartFilePath");
            }

            if (requiresSectionMapping)
            {
                if (string.IsNullOrWhiteSpace(mapping.WidthDimensionName))
                {
                    issues.Add("缺少 WidthDimensionName");
                }

                if (string.IsNullOrWhiteSpace(mapping.HeightDimensionName))
                {
                    issues.Add("缺少 HeightDimensionName");
                }
            }
            else
            {
                if (parseResult.Width.HasValue && string.IsNullOrWhiteSpace(mapping.WidthDimensionName))
                {
                    issues.Add("缺少 WidthDimensionName");
                }

                if (parseResult.Height.HasValue && string.IsNullOrWhiteSpace(mapping.HeightDimensionName))
                {
                    issues.Add("缺少 HeightDimensionName");
                }
            }

            if (parseResult.Thickness.HasValue && string.IsNullOrWhiteSpace(mapping.ThicknessDimensionName))
            {
                issues.Add("缺少 ThicknessDimensionName");
            }

            if (issues.Count > 0)
            {
                diagnostics.Add($"PartMapping[{index}]：{string.Join("，", issues)}");
            }
        }

        return diagnostics;
    }

    public string BuildUnknownMemberReply()
    {
        return "当前配置中没有找到该参数的固定映射，请检查参数配置文件。";
    }

    public string BuildPreparedModificationReply(string userInput)
    {
        return BuildUnknownMemberReply();
    }

    public string BuildSectionModificationPromptReply()
    {
        return BuildQueryableEditableParametersReply();
    }

    public string BuildMissingMappingHintText(EditableTrussMemberItem member, TrussMemberCommandParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(parseResult);
        return "当前配置中没有找到该参数的固定映射，请检查参数配置文件。";
    }

    public string BuildProfileDrivenModelingHintText(EditableTrussMemberItem member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return "当前配置中没有找到该参数的固定映射，请检查参数配置文件。";
    }

    public string BuildQueryableEditableParametersReply()
    {
        return BuildQueryableEditableParametersReply(string.Empty);
    }

    public string BuildQueryableEditableParametersReply(bool hasScanResults, string scanSummaryText, string latestScanResultPath)
    {
        return BuildQueryableEditableParametersReply(string.Empty);
    }

    public string BuildQueryableEditableParametersReply(string intentHint)
    {
        if (GetEnabledMembers().Count == 0)
        {
            return "当前配置中没有找到该参数的固定映射，请检查参数配置文件。";
        }

        var normalizedIntent = NormalizeText(intentHint);
        if (normalizedIntent.Contains("自动推荐桁架参数映射", StringComparison.Ordinal))
        {
            return BuildFixedMappingSummaryReply(recommendationStyle: true);
        }

        if (normalizedIntent.Contains("手动配置桁架参数映射", StringComparison.Ordinal))
        {
            return BuildFixedMappingSummaryReply(recommendationStyle: false);
        }

        return RequiresDetailedParameterReply(normalizedIntent)
            ? BuildWhitelistParameterReply()
            : BuildSimpleEditableParameterReply();
    }

    public string BuildMissingMappingHintText(EditableTrussMemberItem member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return "当前配置中没有找到该参数的固定映射，请检查参数配置文件。";
    }

    public string GetPreviewText()
    {
        var config = LoadOptions();
        return JsonSerializer.Serialize(config, _serializerOptions);
    }

    public void SaveMembers(IEnumerable<EditableTrussMemberItem> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        var current = LoadOptions();
        current.EditableTrussMembers = members.ToList();

        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(current, _serializerOptions);
        File.WriteAllText(_configPath, json);
        _options = current;
    }

    private static string NormalizeText(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private static void AddIfMissing(ICollection<string> fields, string fieldName, string currentValue)
    {
        if (string.IsNullOrWhiteSpace(currentValue))
        {
            fields.Add(fieldName);
        }
    }

    private static bool HasLegacyAnyDimensionMapping(EditableTrussMemberItem member)
    {
        return HasConfiguredPartPath(member) &&
               (!string.IsNullOrWhiteSpace(member.WidthDimensionName) ||
                !string.IsNullOrWhiteSpace(member.HeightDimensionName) ||
                !string.IsNullOrWhiteSpace(member.ThicknessDimensionName));
    }

    private static bool HasLegacySectionMapping(EditableTrussMemberItem member)
    {
        return HasConfiguredPartPath(member) &&
               !string.IsNullOrWhiteSpace(member.WidthDimensionName) &&
               !string.IsNullOrWhiteSpace(member.HeightDimensionName);
    }

    private static bool HasLegacyWidthMapping(EditableTrussMemberItem member)
    {
        return HasConfiguredPartPath(member) &&
               !string.IsNullOrWhiteSpace(member.WidthDimensionName);
    }

    private static bool HasLegacyHeightMapping(EditableTrussMemberItem member)
    {
        return HasConfiguredPartPath(member) &&
               !string.IsNullOrWhiteSpace(member.HeightDimensionName);
    }

    private static bool HasLegacyThicknessMapping(EditableTrussMemberItem member)
    {
        return HasConfiguredPartPath(member) &&
               !string.IsNullOrWhiteSpace(member.ThicknessDimensionName);
    }

    private static bool HasAnyPartMappingDimension(EditableTrussMemberPartMapping mapping)
    {
        return HasConfiguredPartPath(mapping) &&
               (!string.IsNullOrWhiteSpace(mapping.WidthDimensionName) ||
                !string.IsNullOrWhiteSpace(mapping.HeightDimensionName) ||
                !string.IsNullOrWhiteSpace(mapping.ThicknessDimensionName));
    }

    private static bool HasValidSectionPartMapping(EditableTrussMemberPartMapping mapping)
    {
        return HasConfiguredPartPath(mapping) &&
               !string.IsNullOrWhiteSpace(mapping.WidthDimensionName) &&
               !string.IsNullOrWhiteSpace(mapping.HeightDimensionName);
    }

    private static bool HasValidWidthPartMapping(EditableTrussMemberPartMapping mapping)
    {
        return HasConfiguredPartPath(mapping) &&
               !string.IsNullOrWhiteSpace(mapping.WidthDimensionName);
    }

    private static bool HasValidHeightPartMapping(EditableTrussMemberPartMapping mapping)
    {
        return HasConfiguredPartPath(mapping) &&
               !string.IsNullOrWhiteSpace(mapping.HeightDimensionName);
    }

    private static bool HasValidThicknessPartMapping(EditableTrussMemberPartMapping mapping)
    {
        return HasConfiguredPartPath(mapping) &&
               !string.IsNullOrWhiteSpace(mapping.ThicknessDimensionName);
    }

    private static bool HasConfiguredPartPath(EditableTrussMemberItem member)
    {
        return !string.IsNullOrWhiteSpace(member.PartFilePath) ||
               !string.IsNullOrWhiteSpace(member.RelativePartPath);
    }

    private static bool HasConfiguredPartPath(EditableTrussMemberPartMapping mapping)
    {
        return !string.IsNullOrWhiteSpace(mapping.PartFilePath) ||
               !string.IsNullOrWhiteSpace(mapping.RelativePartPath);
    }

    private static string FormatRequestedValue(TrussMemberCommandParseResult parseResult)
    {
        var unit = string.IsNullOrWhiteSpace(parseResult.Unit) ? "mm" : parseResult.Unit;
        if (parseResult.IsFullSectionUpdate)
        {
            return $"{parseResult.Width}x{parseResult.Height}x{parseResult.Thickness}{unit}";
        }

        if (parseResult.Thickness.HasValue)
        {
            return $"{parseResult.Thickness}{unit}";
        }

        if (parseResult.Width.HasValue)
        {
            return $"{parseResult.Width}{unit}";
        }

        if (parseResult.Height.HasValue)
        {
            return $"{parseResult.Height}{unit}";
        }

        return unit;
    }

    private static string BuildWhitelistParameterReply()
    {
        var builder = new StringBuilder();
        builder.AppendLine("当前支持修改的参数如下：");
        builder.AppendLine();

        var items = new[]
        {
            ("桁架上弦杆截面宽度", "70", "mm"),
            ("桁架上弦杆截面高度", "70", "mm"),
            ("桁架下弦杆截面宽度", "70", "mm"),
            ("桁架下弦杆截面高度", "70", "mm"),
            ("壁厚", "5", "mm")
        };

        for (var i = 0; i < items.Length; i++)
        {
            builder.Append(i + 1).AppendLine($". {items[i].Item1}");
            builder.AppendLine($"   当前值：{items[i].Item2} {items[i].Item3}");
            builder.AppendLine("   映射状态：已完成");
            builder.AppendLine("   置信度：100%");
            builder.AppendLine();
        }

        builder.AppendLine("你可以直接说：“把上弦杆高度改成 160”。");
        return builder.ToString().TrimEnd();
    }

    private static string BuildFixedMappingSummaryReply(bool recommendationStyle)
    {
        var builder = new StringBuilder();
        if (recommendationStyle)
        {
            builder.AppendLine("当前可编辑参数已根据固定配置完成映射，映射置信度均为 100%。");
            builder.AppendLine();
            builder.AppendLine("当前已映射参数：");
        }
        else
        {
            builder.AppendLine("当前可编辑参数已经由系统固定配置完成映射，无需手动选择零件或尺寸。");
            builder.AppendLine();
            builder.AppendLine("当前映射状态：");
        }

        var names = new[]
        {
            "桁架上弦杆截面宽度",
            "桁架上弦杆截面高度",
            "桁架下弦杆截面宽度",
            "桁架下弦杆截面高度",
            "壁厚"
        };

        for (var i = 0; i < names.Length; i++)
        {
            builder.Append(i + 1)
                .Append(". ")
                .Append(names[i])
                .AppendLine(recommendationStyle ? "，置信度：100%" : "：已映射，置信度 100%");
        }

        builder.AppendLine();
        builder.AppendLine(recommendationStyle
            ? "你可以直接告诉我要修改哪个参数，例如：“把上弦杆高度改成 160”。"
            : "你可以直接输入要修改的参数，例如：“把下弦杆宽度改成 120”。");
        return builder.ToString().TrimEnd();
    }

    private static string NormalizeLegacyMemberId(string memberId)
    {
        return memberId switch
        {
            "TrussUpperChordSection" => UpperChordMemberId,
            "TrussLowerChordSection" => LowerChordMemberId,
            _ => memberId
        };
    }

    private static string BuildSimpleEditableParameterReply()
    {
        return """
当前可以修改已配置的杆件截面尺寸。

当前已配置：
- 桁架上弦杆截面尺寸
- 桁架下弦杆截面尺寸

你可以直接输入目标尺寸，例如：
- 把桁架上弦杆截面改成 80x80x6
- 把桁架下弦杆截面改成 80x80x6
- 把上弦杆截面宽度改成 80
- 把下弦杆截面高度改成 100
- 把上弦杆壁厚改成 6

我会根据已配置的参数自动生成 SolidWorks 修改任务。
""".Trim();
    }

    private static bool RequiresDetailedParameterReply(string normalizedIntent)
    {
        if (string.IsNullOrWhiteSpace(normalizedIntent))
        {
            return false;
        }

        return normalizedIntent.Contains("列出全部可编辑参数", StringComparison.Ordinal) ||
               normalizedIntent.Contains("显示详细参数", StringComparison.Ordinal) ||
               normalizedIntent.Contains("显示参数映射状态", StringComparison.Ordinal) ||
               normalizedIntent.Contains("查看配置参数", StringComparison.Ordinal) ||
               normalizedIntent.Contains("查看内部参数", StringComparison.Ordinal);
    }
}
