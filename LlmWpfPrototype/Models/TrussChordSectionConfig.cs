using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LlmWpfPrototype.Models;

public sealed class TrussChordSectionConfig
{
    [JsonPropertyName("section_spec_delimiters")]
    public List<string> SectionSpecDelimiters { get; set; } = [];

    [JsonPropertyName("composite_parameters")]
    public List<TrussChordCompositeParameterConfig> CompositeParameters { get; set; } = [];
}

public sealed class TrussChordCompositeParameterConfig
{
    [JsonPropertyName("composite_parameter_name")]
    public string CompositeParameterName { get; set; } = string.Empty;

    [JsonPropertyName("targets")]
    public List<TrussChordAtomicParameterTarget> Targets { get; set; } = [];
}

public sealed class TrussChordAtomicParameterTarget
{
    [JsonPropertyName("parameter_name")]
    public string ParameterName { get; set; } = string.Empty;

    [JsonPropertyName("value_index")]
    public int ValueIndex { get; set; }
}
