using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services;

public sealed class DimensionScanCatalogService
{
    private readonly string _jsonPath;
    private readonly string _csvPath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public DimensionScanCatalogService()
    {
        var reportsDirectory = Path.Combine(AppContext.BaseDirectory, "reports");
        _jsonPath = Path.Combine(reportsDirectory, "dimension-scan-result.json");
        _csvPath = Path.Combine(reportsDirectory, "dimension-scan-result.csv");
    }

    public string JsonPath => _jsonPath;

    public string CsvPath => _csvPath;

    public string LatestDimensionScanResultPath => _jsonPath;

    public bool HasLatestScanResult()
    {
        return File.Exists(_jsonPath);
    }

    public DateTime? GetLatestScanTime()
    {
        return File.Exists(_jsonPath)
            ? File.GetLastWriteTime(_jsonPath)
            : null;
    }

    public SolidWorksDimensionScanResult LoadLatestResultOrEmpty()
    {
        if (!File.Exists(_jsonPath))
        {
            return new SolidWorksDimensionScanResult
            {
                JsonOutputPath = _jsonPath,
                CsvOutputPath = _csvPath
            };
        }

        var json = File.ReadAllText(_jsonPath);
        var result = JsonSerializer.Deserialize<SolidWorksDimensionScanResult>(json, _serializerOptions)
            ?? new SolidWorksDimensionScanResult();
        result.JsonOutputPath = string.IsNullOrWhiteSpace(result.JsonOutputPath) ? _jsonPath : result.JsonOutputPath;
        result.CsvOutputPath = string.IsNullOrWhiteSpace(result.CsvOutputPath) ? _csvPath : result.CsvOutputPath;
        return result;
    }

    public IReadOnlyList<string> GetDistinctPartFilePaths()
    {
        return LoadLatestResultOrEmpty()
            .Items
            .Select(item => item.PartFilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetDistinctComponentNames()
    {
        return LoadLatestResultOrEmpty()
            .Items
            .Select(item => item.ComponentName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetPartFilePathsByComponent(string? componentName)
    {
        return GetItems(componentName, null)
            .Select(item => item.PartFilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<SolidWorksDimensionScanItem> GetItems(string? componentName, string? partFilePath)
    {
        var items = LoadLatestResultOrEmpty().Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(componentName))
        {
            items = items.Where(item => string.Equals(item.ComponentName, componentName, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(partFilePath))
        {
            items = items.Where(item => string.Equals(item.PartFilePath, partFilePath, StringComparison.OrdinalIgnoreCase));
        }

        return items.ToList();
    }

    public IReadOnlyList<SolidWorksDimensionScanItem> GetItemsForPart(string? partFilePath)
    {
        return GetItems(null, partFilePath);
    }

    public IReadOnlyList<string> GetDimensionOptions(string? partFilePath)
    {
        return GetItemsForPart(partFilePath)
            .Select(GetPreferredDimensionName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetDimensionOptions(string? componentName, string? partFilePath)
    {
        return GetItems(componentName, partFilePath)
            .Select(GetPreferredDimensionName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetComponentName(string? partFilePath)
    {
        return GetItemsForPart(partFilePath)
            .Select(item => item.ComponentName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? string.Empty;
    }

    public SolidWorksDimensionScanItem? FindDimension(string? componentName, string? partFilePath, string? dimensionName)
    {
        if (string.IsNullOrWhiteSpace(dimensionName))
        {
            return null;
        }

        return GetItems(componentName, partFilePath)
            .FirstOrDefault(item =>
                string.Equals(item.FullDimensionName, dimensionName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.DimensionName, dimensionName, StringComparison.OrdinalIgnoreCase));
    }

    public string BuildScanSummaryText()
    {
        var result = LoadLatestResultOrEmpty();
        if (result.Items.Count == 0)
        {
            return "当前还没有可用的尺寸扫描结果。请先执行“扫描模型尺寸”。";
        }

        var scanTime = GetLatestScanTime();
        return $"已经完成尺寸扫描。当前共有 {result.Items.Count} 条候选尺寸、{result.Errors.Count} 条读取失败，扫描时间 {scanTime:yyyy-MM-dd HH:mm:ss}。";
    }

    private static string GetPreferredDimensionName(SolidWorksDimensionScanItem item)
    {
        return string.IsNullOrWhiteSpace(item.FullDimensionName)
            ? item.DimensionName
            : item.FullDimensionName;
    }
}
