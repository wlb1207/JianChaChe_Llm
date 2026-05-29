using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class TrussChordSectionConfigService
{
    private readonly string _configPath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private TrussChordSectionConfig? _config;

    public TrussChordSectionConfigService()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "Config", "truss_chord_section_config.json");
    }

    public bool ConfigExists()
    {
        return File.Exists(_configPath);
    }

    public string GetConfigPath()
    {
        return _configPath;
    }

    public TrussChordSectionConfig LoadConfig()
    {
        if (_config is not null)
        {
            return _config;
        }

        if (!File.Exists(_configPath))
        {
            throw new FileNotFoundException($"缺少桁架弦杆截面参数配置文件：{_configPath}");
        }

        var json = File.ReadAllText(_configPath);
        _config = JsonSerializer.Deserialize<TrussChordSectionConfig>(json, _serializerOptions)
            ?? new TrussChordSectionConfig();
        return _config;
    }

    public TrussChordCompositeParameterConfig? FindCompositeParameter(string parameterName)
    {
        return LoadConfig().CompositeParameters.FirstOrDefault(item =>
            string.Equals(item.CompositeParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
    }

    public string GetPreviewText()
    {
        var config = LoadConfig();
        return JsonSerializer.Serialize(config, _serializerOptions);
    }
}
