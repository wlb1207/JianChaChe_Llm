using System.IO;
using System.Text.Json;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services.Simulation;

namespace LlmWpfPrototype.Services;

public static class AppConfigLoader
{
    // Compiled-in service defaults so plaintext Config/appsettings.json does not leak private endpoint or model names.
    // Can still be overridden explicitly via appsettings.json or environment variables (SANLIU_LLM_*).
    private static class BuiltInServiceDefaults
    {
        public const string Mode = "Cloud";
        public const string Provider = "OpenAI";
        public const string ApiBaseUrl = "https://api.kgnas.top/v1";
        public const string ApiKeyEnvName = "OPENAI_API_KEY";
        public const string Model = "gpt-6.1-sol";
        public const int TimeoutSeconds = 180;
    }

    public static LlmOptions Load()
    {
        return LoadSettings().Llm;
    }

    public static AppSettings LoadSettings()
    {
        var configPath = GetConfigPath();
        if (!File.Exists(configPath))
        {
            return new AppSettings
            {
                Llm = ApplyLlmDefaults(new LlmOptions()),
                SolidWorksWorkspace = WorkspacePathResolver.ResolveWorkspaceOptions(new SolidWorksWorkspaceOptions()),
                Simulation = ResolveSimulationOptions(new SimulationOptions())
            };
        }

        var json = File.ReadAllText(configPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new AppSettings
        {
            Llm = ApplyLlmDefaults(ReadLlmOptions(root)),
            SolidWorksWorkspace = WorkspacePathResolver.ResolveWorkspaceOptions(ReadSolidWorksWorkspaceOptions(root)),
            Simulation = ResolveSimulationOptions(ReadSimulationOptions(root))
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

    public static void SaveSettings(AppSettings settings, string? targetPath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = string.IsNullOrWhiteSpace(targetPath) ? GetConfigPath() : targetPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = new
        {
            Llm = new
            {
                Mode = string.IsNullOrWhiteSpace(settings.Llm.Mode) ? "Cloud" : settings.Llm.Mode.Trim(),
                Provider = string.IsNullOrWhiteSpace(settings.Llm.Provider) ? "OpenAI" : settings.Llm.Provider.Trim(),
                ApiBaseUrl = settings.Llm.ApiBaseUrl?.Trim() ?? string.Empty,
                ApiKeyEnvName = settings.Llm.ApiKeyEnvName?.Trim() ?? string.Empty,
                Model = settings.Llm.Model?.Trim() ?? string.Empty,
                TimeoutSeconds = Math.Clamp(settings.Llm.TimeoutSeconds, 10, 600)
            },
            Simulation = new
            {
                LauncherPath = settings.Simulation.LauncherPath?.Trim() ?? string.Empty,
                SolverPath = settings.Simulation.SolverPath?.Trim() ?? string.Empty,
                StudyFile = settings.Simulation.StudyFile?.Trim() ?? string.Empty
            },
            SolidWorksWorkspace = new
            {
                ModelRootPath = settings.SolidWorksWorkspace.ModelRootPath?.Trim() ?? string.Empty,
                InitialModelFolder = settings.SolidWorksWorkspace.InitialModelFolder?.Trim() ?? string.Empty,
                WorkingModelFolder = settings.SolidWorksWorkspace.WorkingModelFolder?.Trim() ?? string.Empty,
                AssemblyFileName = settings.SolidWorksWorkspace.AssemblyFileName?.Trim() ?? string.Empty
            }
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, path, overwrite: true);
    }

    private static LlmOptions ApplyLlmDefaults(LlmOptions raw)
    {
        var envMode = Environment.GetEnvironmentVariable("SANLIU_LLM_MODE");
        var envBaseUrl = Environment.GetEnvironmentVariable("SANLIU_LLM_BASE_URL");
        var envModel = Environment.GetEnvironmentVariable("SANLIU_LLM_MODEL");
        var envKeyName = Environment.GetEnvironmentVariable("SANLIU_LLM_API_KEY_ENV");

        return new LlmOptions
        {
            Mode = FirstNonEmpty(envMode, raw.Mode, BuiltInServiceDefaults.Mode),
            Provider = FirstNonEmpty(raw.Provider, BuiltInServiceDefaults.Provider),
            ApiBaseUrl = FirstNonEmpty(envBaseUrl, raw.ApiBaseUrl, BuiltInServiceDefaults.ApiBaseUrl),
            ApiKeyEnvName = FirstNonEmpty(envKeyName, raw.ApiKeyEnvName, BuiltInServiceDefaults.ApiKeyEnvName),
            Model = FirstNonEmpty(envModel, raw.Model, BuiltInServiceDefaults.Model),
            TimeoutSeconds = raw.TimeoutSeconds > 0 ? raw.TimeoutSeconds : BuiltInServiceDefaults.TimeoutSeconds,
            AlwaysUseLlmForLocalReplies = raw.AlwaysUseLlmForLocalReplies
        };
    }

    private static SimulationOptions ResolveSimulationOptions(SimulationOptions raw)
    {
        var detected = AnsysInstallationDetector.DetectBestInstallation(raw.LauncherPath, raw.SolverPath);
        return new SimulationOptions
        {
            LauncherPath = !string.IsNullOrWhiteSpace(raw.LauncherPath) && File.Exists(raw.LauncherPath)
                ? raw.LauncherPath
                : (detected?.LauncherPath ?? raw.LauncherPath ?? string.Empty),
            SolverPath = !string.IsNullOrWhiteSpace(raw.SolverPath) && File.Exists(raw.SolverPath)
                ? raw.SolverPath
                : (detected?.SolverPath ?? raw.SolverPath ?? string.Empty),
            StudyFile = raw.StudyFile ?? string.Empty
        };
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c)) return c.Trim();
        }
        return string.Empty;
    }

    private static SimulationOptions ReadSimulationOptions(JsonElement root)
    {
        if (!root.TryGetProperty("Simulation", out var section) || section.ValueKind != JsonValueKind.Object)
            return new SimulationOptions();

        string ReadPath(string name) =>
            section.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        return new SimulationOptions
        {
            LauncherPath = ReadPath("LauncherPath"),
            SolverPath = ReadPath("SolverPath"),
            StudyFile = ReadPath("StudyFile")
        };
    }

    private static LlmOptions ReadLlmOptions(JsonElement root)
    {
        if (!root.TryGetProperty("Llm", out var llmSection))
        {
            return new LlmOptions();
        }

        return new LlmOptions
        {
            Mode = llmSection.TryGetProperty("Mode", out var mode) ? mode.GetString() ?? string.Empty : string.Empty,
            Provider = llmSection.TryGetProperty("Provider", out var provider) ? provider.GetString() ?? string.Empty : string.Empty,
            ApiBaseUrl = llmSection.TryGetProperty("ApiBaseUrl", out var baseUrl) ? baseUrl.GetString() ?? string.Empty : string.Empty,
            ApiKeyEnvName = llmSection.TryGetProperty("ApiKeyEnvName", out var apiKeyEnvName) ? apiKeyEnvName.GetString() ?? string.Empty : string.Empty,
            Model = llmSection.TryGetProperty("Model", out var model) ? model.GetString() ?? string.Empty : string.Empty,
            TimeoutSeconds = llmSection.TryGetProperty("TimeoutSeconds", out var timeoutSeconds) && timeoutSeconds.TryGetInt32(out var seconds)
                ? seconds
                : BuiltInServiceDefaults.TimeoutSeconds,
            AlwaysUseLlmForLocalReplies = llmSection.TryGetProperty("AlwaysUseLlmForLocalReplies", out var alwaysUseLlmForLocalReplies) &&
                                          alwaysUseLlmForLocalReplies.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? alwaysUseLlmForLocalReplies.GetBoolean()
                : true
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
