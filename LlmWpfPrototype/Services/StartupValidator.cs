using System.IO;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public static class StartupValidator
{
    public static StartupValidationResult Validate()
    {
        var configPath = AppConfigLoader.GetConfigPath();
        if (!File.Exists(configPath))
        {
            return StartupValidationResult.Invalid($"缺少配置文件：{configPath}");
        }

        LlmOptions options;
        try
        {
            options = AppConfigLoader.LoadOrThrow();
        }
        catch (Exception ex)
        {
            return StartupValidationResult.Invalid(ex.Message);
        }

        var chordSectionConfigService = new TrussChordSectionConfigService();
        if (!chordSectionConfigService.ConfigExists())
        {
            return StartupValidationResult.Invalid($"缺少桁架弦杆截面参数配置文件：{chordSectionConfigService.GetConfigPath()}");
        }

        var editableTrussMemberCatalogService = new EditableTrussMemberCatalogService();
        if (!editableTrussMemberCatalogService.ConfigExists())
        {
            return StartupValidationResult.Invalid($"缺少可编辑桁架弦杆配置文件：{editableTrussMemberCatalogService.GetConfigPath()}");
        }

        var trussMemberCommandParser = new TrussMemberCommandParser(editableTrussMemberCatalogService);
        var parserValidationErrors = trussMemberCommandParser.ValidateBuiltInExamples();
        if (parserValidationErrors.Count > 0)
        {
            return StartupValidationResult.Invalid($"桁架弦杆截面命令解析器自检失败：{string.Join("；", parserValidationErrors)}");
        }

        var dictionaryService = new ParameterDictionaryService(chordSectionConfigService);
        if (!dictionaryService.DictionaryExists())
        {
            return StartupValidationResult.Invalid($"缺少参数字典文件：{dictionaryService.GetDictionaryPath()}");
        }

        if (!IsSupportedMode(options.Mode))
        {
            return StartupValidationResult.Invalid($"不支持的 LLM 模式：{options.Mode}");
        }

        if (!string.Equals(options.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Provider, "OpenAICompatible", StringComparison.OrdinalIgnoreCase))
        {
            return StartupValidationResult.Invalid($"当前仅支持 OpenAI 或 OpenAICompatible Provider，收到：{options.Provider}");
        }

        if (string.IsNullOrWhiteSpace(options.ApiBaseUrl))
        {
            return StartupValidationResult.Invalid("配置项 ApiBaseUrl 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(options.Model) &&
            !string.Equals(options.Mode, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            return StartupValidationResult.Invalid("非 Mock 模式下，配置项 Model 不能为空。");
        }

        if (options.TimeoutSeconds <= 0)
        {
            return StartupValidationResult.Invalid("配置项 TimeoutSeconds 必须大于 0。");
        }

        if (string.Equals(options.Mode, "Cloud", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = GetCloudApiKey(options.ApiKeyEnvName);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return StartupValidationResult.Valid("Cloud 模式未检测到 API Key，程序将继续启动，并在首次调用大模型时提示。");
            }
        }

        return StartupValidationResult.Valid("启动自检通过。");
    }

    private static bool IsSupportedMode(string mode)
    {
        return string.Equals(mode, "Mock", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, "Cloud", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCloudApiKey(string envName)
    {
        if (!string.IsNullOrWhiteSpace(envName))
        {
            var configuredKey = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(configuredKey))
            {
                return configuredKey;
            }
        }

        return Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
    }
}
