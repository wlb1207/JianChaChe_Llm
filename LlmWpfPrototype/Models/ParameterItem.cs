using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace LlmWpfPrototype.Models;

public sealed class ParameterItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    public List<string> Targets { get; set; } = [];

    public double Confidence { get; set; }

    public string MappingStatus { get; set; } = "PendingMapping";

    public string DisplayName { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public List<SolidWorksTarget> SolidWorksTargets { get; set; } = [];

    public List<ApdlReplacement> ApdlReplacements { get; set; } = [];

    public string TargetSummary => Targets.Count == 0
        ? "待识别"
        : string.Join(" / ", Targets
            .Select(NormalizeTarget)
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Distinct());

    private static string NormalizeTarget(string target)
    {
        if (target.Contains("solidworks", System.StringComparison.OrdinalIgnoreCase))
        {
            return "SolidWorks";
        }

        if (target.Contains("apdl", System.StringComparison.OrdinalIgnoreCase)
            || target.Contains("mapdl", System.StringComparison.OrdinalIgnoreCase))
        {
            return "APDL";
        }

        return target;
    }
}
