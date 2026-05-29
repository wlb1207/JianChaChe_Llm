namespace LlmWpfPrototype.Models;

public sealed class ApdlGenerationLogEntry
{
    public string ParameterName { get; set; } = string.Empty;

    public string ReplacementMode { get; set; } = string.Empty;

    public string OriginalValue { get; set; } = string.Empty;

    public string NewValue { get; set; } = string.Empty;

    public string OutputFilePath { get; set; } = string.Empty;

    public bool IsWarning { get; set; }

    public string Message { get; set; } = string.Empty;
}
