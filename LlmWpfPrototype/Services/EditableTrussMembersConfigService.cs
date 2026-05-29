using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class EditableTrussMembersConfigService
{
    private readonly string _configPath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private EditableTrussMembersConfig? _config;

    public EditableTrussMembersConfigService()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "Config", "editable-truss-members.json");
    }

    public bool ConfigExists()
    {
        return File.Exists(_configPath);
    }

    public string GetConfigPath()
    {
        return _configPath;
    }

    public EditableTrussMembersConfig LoadConfig()
    {
        if (_config is not null)
        {
            return _config;
        }

        if (!File.Exists(_configPath))
        {
            throw new FileNotFoundException($"缺少可编辑桁架弦杆配置文件：{_configPath}");
        }

        var json = File.ReadAllText(_configPath);
        _config = JsonSerializer.Deserialize<EditableTrussMembersConfig>(json, _serializerOptions)
            ?? new EditableTrussMembersConfig();
        return _config;
    }

    public string GetPreviewText()
    {
        var config = LoadConfig();
        return JsonSerializer.Serialize(config, _serializerOptions);
    }
}
