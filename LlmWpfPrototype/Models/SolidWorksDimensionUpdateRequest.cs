namespace LlmWpfPrototype.Models;

public sealed class SolidWorksDimensionUpdateRequest
{
    public string ParameterName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PartFilePath { get; set; } = string.Empty;

    public string Configuration { get; set; } = string.Empty;

    public string DimensionName { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;
}
