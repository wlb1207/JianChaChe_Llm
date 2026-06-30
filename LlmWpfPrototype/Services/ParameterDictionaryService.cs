using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class ParameterDictionaryService
{
    private readonly string _dictionaryPath;
    private readonly TrussChordSectionConfigService _trussChordSectionConfigService;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private List<ParameterDefinition>? _definitions;

    public ParameterDictionaryService(TrussChordSectionConfigService trussChordSectionConfigService)
    {
        _trussChordSectionConfigService = trussChordSectionConfigService;
        _dictionaryPath = AppPaths.GetBundledConfigPath("parameter_dictionary.json");
    }

    public IReadOnlyList<ParameterDefinition> LoadDefinitions()
    {
        if (_definitions is not null)
        {
            return _definitions;
        }

        if (!File.Exists(_dictionaryPath))
        {
            throw new FileNotFoundException($"缺少参数字典文件：{_dictionaryPath}");
        }

        var json = File.ReadAllText(_dictionaryPath);
        _definitions = JsonSerializer.Deserialize<List<ParameterDefinition>>(json, _serializerOptions) ?? [];
        return _definitions;
    }

    public IReadOnlyList<ParameterItem> EnrichParameters(IEnumerable<ParameterItem> parameters)
    {
        var definitions = LoadDefinitions();
        var normalizedParameters = NormalizeParameters(parameters, definitions);
        var enrichedParameters = new List<ParameterItem>();

        foreach (var parameter in normalizedParameters)
        {
            enrichedParameters.Add(EnrichParameter(parameter, definitions));
        }

        return enrichedParameters;
    }

    public IReadOnlyList<ParameterItem> BuildMockParameters()
    {
        return LoadDefinitions()
            .Where(definition =>
                definition.Name.EndsWith("_width", StringComparison.OrdinalIgnoreCase) ||
                definition.Name.EndsWith("_height", StringComparison.OrdinalIgnoreCase) ||
                definition.Name.EndsWith("_thickness", StringComparison.OrdinalIgnoreCase))
            .Select(definition => new ParameterItem
            {
                Name = definition.Name,
                Value = definition.DefaultValue,
                Unit = definition.Unit
            })
            .ToList();
    }

    public bool DictionaryExists()
    {
        return File.Exists(_dictionaryPath);
    }

    public string GetDictionaryPath()
    {
        return _dictionaryPath;
    }

    private static ParameterItem EnrichParameter(ParameterItem parameter, IReadOnlyList<ParameterDefinition> definitions)
    {
        var definition = FindDefinition(parameter.Name, parameter.DisplayName, definitions);
        if (definition is null)
        {
            parameter.MappingStatus = "PendingMapping";
            return parameter;
        }

        parameter.DisplayName = definition.DisplayName;
        parameter.Category = definition.Category;
        parameter.Description = definition.Description;
        parameter.SolidWorksTargets = definition.SolidWorksTargets
            .Select(target => new SolidWorksTarget
            {
                SourceFile = target.SourceFile,
                OutputFile = target.OutputFile,
                Configuration = target.Configuration,
                DimensionName = target.DimensionName,
                Unit = target.Unit
            })
            .ToList();
        parameter.ApdlReplacements = definition.ApdlReplacements
            .Select(replacement => new ApdlReplacement
            {
                SourceFile = replacement.SourceFile,
                OutputFile = replacement.OutputFile,
                Mode = NormalizeMode(replacement.Mode),
                VariableName = replacement.VariableName,
                Command = replacement.Command,
                Occurrence = replacement.Occurrence,
                ArgumentIndex = replacement.ArgumentIndex,
                RegexPattern = replacement.RegexPattern,
                RegexReplacement = replacement.RegexReplacement,
                Description = replacement.Description
            })
            .ToList();

        if (string.IsNullOrWhiteSpace(parameter.Unit))
        {
            parameter.Unit = definition.Unit;
        }

        if (!string.Equals(parameter.Unit, definition.Unit, StringComparison.OrdinalIgnoreCase))
        {
            parameter.MappingStatus = "UnitMismatch";
            return parameter;
        }

        if (IsOutOfRange(parameter.Value, definition))
        {
            parameter.MappingStatus = "OutOfRange";
            return parameter;
        }

        if (parameter.SolidWorksTargets.Count == 0 && parameter.ApdlReplacements.Count == 0)
        {
            parameter.MappingStatus = "PendingMapping";
            return parameter;
        }

        parameter.MappingStatus = "OK";
        return parameter;
    }

    private IReadOnlyList<ParameterItem> NormalizeParameters(
        IEnumerable<ParameterItem> parameters,
        IReadOnlyList<ParameterDefinition> definitions)
    {
        var expandedParameters = new List<ParameterItem>();

        foreach (var parameter in parameters)
        {
            if (TryExpandCompositeSectionParameter(parameter, definitions, out var expanded))
            {
                expandedParameters.AddRange(expanded);
                continue;
            }

            expandedParameters.Add(parameter);
        }

        return MergeDuplicateParameters(expandedParameters);
    }

    private bool TryExpandCompositeSectionParameter(
        ParameterItem parameter,
        IReadOnlyList<ParameterDefinition> definitions,
        out IReadOnlyList<ParameterItem> expandedParameters)
    {
        expandedParameters = [];

        var definition = FindDefinition(parameter.Name, parameter.DisplayName, definitions);
        if (definition is null)
        {
            return false;
        }

        var compositeConfig = _trussChordSectionConfigService.FindCompositeParameter(definition.Name);
        if (compositeConfig is null)
        {
            return false;
        }

        if (!TryParseSectionSpec(parameter.Value, _trussChordSectionConfigService.LoadConfig().SectionSpecDelimiters, out var values))
        {
            return false;
        }

        var unit = string.IsNullOrWhiteSpace(parameter.Unit) ? "mm" : parameter.Unit;
        expandedParameters = BuildExpandedParameters(compositeConfig, definitions, values, unit, parameter.Confidence, parameter.Targets);

        return expandedParameters.Count > 0;
    }

    private static List<ParameterItem> BuildExpandedParameters(
        TrussChordCompositeParameterConfig compositeConfig,
        IReadOnlyList<ParameterDefinition> definitions,
        IReadOnlyList<decimal> values,
        string unit,
        double confidence,
        IReadOnlyList<string> targets)
    {
        var expandedParameters = new List<ParameterItem>();

        foreach (var target in compositeConfig.Targets)
        {
            if (target.ValueIndex < 0 || target.ValueIndex >= values.Count)
            {
                continue;
            }

            var definition = definitions.FirstOrDefault(item =>
                string.Equals(item.Name, target.ParameterName, StringComparison.OrdinalIgnoreCase));
            var displayName = definition?.DisplayName ?? target.ParameterName;

            expandedParameters.Add(CreateExpandedParameter(
                target.ParameterName,
                displayName,
                values[target.ValueIndex],
                unit,
                confidence,
                targets));
        }

        return expandedParameters;
    }

    private static ParameterItem CreateExpandedParameter(
        string name,
        string displayName,
        decimal value,
        string unit,
        double confidence,
        IReadOnlyList<string> targets)
    {
        return new ParameterItem
        {
            Name = name,
            DisplayName = displayName,
            Value = value.ToString(CultureInfo.InvariantCulture),
            Unit = unit,
            Confidence = confidence,
            Targets = targets.ToList()
        };
    }

    private static bool TryParseSectionSpec(string value, IReadOnlyList<string> delimiters, out IReadOnlyList<decimal> values)
    {
        values = [];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var delimiterPattern = BuildDelimiterPattern(delimiters);
        var match = Regex.Match(
            value.Trim(),
            $@"^(?<v0>\d+(?:\.\d+)?)\s*(?:{delimiterPattern})\s*(?<v1>\d+(?:\.\d+)?)\s*(?:{delimiterPattern})\s*(?<v2>\d+(?:\.\d+)?)\s*(?:mm)?$",
            RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return false;
        }

        var parsedValues = new List<decimal>();
        for (var index = 0; index < 3; index++)
        {
            if (!decimal.TryParse(match.Groups[$"v{index}"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue))
            {
                return false;
            }

            parsedValues.Add(parsedValue);
        }

        values = parsedValues;
        return true;
    }

    private static string BuildDelimiterPattern(IReadOnlyList<string> delimiters)
    {
        var effectiveDelimiters = delimiters.Count == 0 ? new[] { "x", "X", "×" } : delimiters;
        return string.Join("|", effectiveDelimiters.Select(Regex.Escape));
    }

    private static IReadOnlyList<ParameterItem> MergeDuplicateParameters(IEnumerable<ParameterItem> parameters)
    {
        var mergedParameters = new List<ParameterItem>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters)
        {
            var key = ResolveMergeKey(parameter);
            if (indexByKey.TryGetValue(key, out var index))
            {
                mergedParameters[index] = parameter;
                continue;
            }

            indexByKey[key] = mergedParameters.Count;
            mergedParameters.Add(parameter);
        }

        return mergedParameters;
    }

    private static string ResolveMergeKey(ParameterItem parameter)
    {
        if (!string.IsNullOrWhiteSpace(parameter.Name))
        {
            return parameter.Name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parameter.DisplayName))
        {
            return parameter.DisplayName.Trim();
        }

        return Guid.NewGuid().ToString("N");
    }

    private static ParameterDefinition? FindDefinition(
        string parameterName,
        string displayName,
        IEnumerable<ParameterDefinition> definitions)
    {
        return definitions.FirstOrDefault(definition =>
            MatchesDefinition(definition, parameterName) ||
            MatchesDefinition(definition, displayName));
    }

    private static bool MatchesDefinition(ParameterDefinition definition, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return string.Equals(definition.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(definition.DisplayName, candidate, StringComparison.OrdinalIgnoreCase) ||
               definition.Aliases.Any(alias => string.Equals(alias, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeMode(string mode)
    {
        if (string.Equals(mode, "variable", StringComparison.OrdinalIgnoreCase))
        {
            return "VariableAssignment";
        }

        if (string.Equals(mode, "command_argument", StringComparison.OrdinalIgnoreCase))
        {
            return "CommandArgument";
        }

        return mode;
    }

    private static bool IsOutOfRange(string value, ParameterDefinition definition)
    {
        if (!definition.MinValue.HasValue && !definition.MaxValue.HasValue)
        {
            return false;
        }

        if (!TryParseDecimal(value, out var numericValue))
        {
            return false;
        }

        if (definition.MinValue.HasValue && numericValue < definition.MinValue.Value)
        {
            return true;
        }

        if (definition.MaxValue.HasValue && numericValue > definition.MaxValue.Value)
        {
            return true;
        }

        return false;
    }

    private static bool TryParseDecimal(string value, out decimal numericValue)
    {
        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out numericValue)
            || decimal.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out numericValue);
    }
}
