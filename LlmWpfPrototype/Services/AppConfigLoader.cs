using System.IO;
using System.Text.Json;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public static class AppConfigLoader
{
    public static LlmOptions Load()
    {
        return LoadSettings().Llm;
    }

    public static AppSettings LoadSettings()
    {
        var configPath = GetConfigPath();
        if (!File.Exists(configPath))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(configPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new AppSettings
        {
            Llm = ReadLlmOptions(root),
            SolidWorksWorkspace = ReadSolidWorksWorkspaceOptions(root)
        };
    }

    public static LlmOptions LoadOrThrow()
    {
        return LoadSettingsOrThrow().Llm;
    }

    public static AppSettings LoadSettingsOrThrow()
    {
        var configPath = GetConfigPath();
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"缺少配置文件：{configPath}");
        }

        try
        {
            return LoadSettings();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"配置文件格式错误：{configPath}", ex);
        }
    }

    public static string GetConfigPath()
    {
        return AppPaths.GetBundledConfigPath("appsettings.json");
    }

    private static LlmOptions ReadLlmOptions(JsonElement root)
    {
        if (!root.TryGetProperty("Llm", out var llmSection))
        {
            return new LlmOptions();
        }

        return new LlmOptions
        {
            Mode = llmSection.TryGetProperty("Mode", out var mode) ? mode.GetString() ?? "Mock" : "Mock",
            Provider = llmSection.TryGetProperty("Provider", out var provider) ? provider.GetString() ?? "OpenAICompatible" : "OpenAICompatible",
            ApiBaseUrl = llmSection.TryGetProperty("ApiBaseUrl", out var baseUrl) ? baseUrl.GetString() ?? string.Empty : string.Empty,
            ApiKeyEnvName = llmSection.TryGetProperty("ApiKeyEnvName", out var apiKeyEnvName) ? apiKeyEnvName.GetString() ?? string.Empty : string.Empty,
            Model = llmSection.TryGetProperty("Model", out var model) ? model.GetString() ?? string.Empty : string.Empty,
            TimeoutSeconds = llmSection.TryGetProperty("TimeoutSeconds", out var timeoutSeconds) && timeoutSeconds.TryGetInt32(out var seconds)
                ? seconds
                : 60
        };
    }

    private static SolidWorksWorkspaceOptions ReadSolidWorksWorkspaceOptions(JsonElement root)
    {
        if (!root.TryGetProperty("SolidWorksWorkspace", out var workspaceSection))
        {
            return new SolidWorksWorkspaceOptions();
        }

        return new SolidWorksWorkspaceOptions
        {
            ModelRootPath = workspaceSection.TryGetProperty("ModelRootPath", out var modelRootPath)
                ? modelRootPath.GetString() ?? string.Empty
                : string.Empty,
            InitialModelFolder = workspaceSection.TryGetProperty("InitialModelFolder", out var initialModelFolder)
                ? initialModelFolder.GetString() ?? string.Empty
                : string.Empty,
            WorkingModelFolder = workspaceSection.TryGetProperty("WorkingModelFolder", out var workingModelFolder)
                ? workingModelFolder.GetString() ?? string.Empty
                : string.Empty,
            AssemblyFileName = workspaceSection.TryGetProperty("AssemblyFileName", out var assemblyFileName)
                ? assemblyFileName.GetString() ?? string.Empty
                : string.Empty
        };
    }
}
