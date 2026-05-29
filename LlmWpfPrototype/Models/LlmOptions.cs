namespace LlmWpfPrototype.Models;

public sealed class LlmOptions
{
    public string Mode { get; set; } = "Mock";

    public string Provider { get; set; } = "OpenAICompatible";

    public string ApiBaseUrl { get; set; } = string.Empty;

    public string ApiKeyEnvName { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 180;

    public bool PrivacySafeLogging { get; set; } = true;

    public bool DebugVerboseLogging { get; set; }
}
