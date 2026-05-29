namespace LlmWpfPrototype.Models;

public sealed class SolidWorksWorkspaceOptions
{
    public string ModelRootPath { get; set; } = string.Empty;

    public string InitialModelFolder { get; set; } = string.Empty;

    public string WorkingModelFolder { get; set; } = string.Empty;

    public string AssemblyFileName { get; set; } = string.Empty;
}
