namespace LlmWpfPrototype.Models;

public sealed class AppSettings
{
    public LlmOptions Llm { get; set; } = new();

    public SolidWorksWorkspaceOptions SolidWorksWorkspace { get; set; } = new();
}
