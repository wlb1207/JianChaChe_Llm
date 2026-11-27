using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LlmWpfPrototype.Services.Simulation;

public sealed record ApdlBatchRunnerOptions(
    string MechanicalApdlExecutablePath,
    string ReviewedTemplatePath,
    string ReviewedTemplateSha256,
    string RunRootPath,
    TimeSpan Timeout)
{
    public IReadOnlyList<string> ProtectedPaths { get; init; } = [];
}

public sealed record ApdlBatchRequest(
    string CaseLabel,
    IReadOnlyDictionary<string, double> NumericBindings,
    IReadOnlySet<string> RequiredMetrics)
{
    public IReadOnlyDictionary<string, string> RequiredMetricUnits { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ApdlMetric(string Name, double Value, string Unit);

public enum ApdlBatchRunStage
{
    Validation,
    WorkspaceCreation,
    InputPreparation,
    SolverExecution,
    ResultParsing,
    Completed
}

public sealed record ApdlBatchRunResult(
    bool Succeeded,
    ApdlBatchRunStage Stage,
    string CaseDirectory,
    int? ExitCode,
    IReadOnlyList<ApdlMetric> Metrics,
    string Message);

/// <summary>
/// Runs only an explicitly reviewed, hash-pinned, self-contained APDL input deck.
/// The deck must write its required results to metrics.csv in the current job directory.
/// </summary>
public sealed class ApdlBatchRunner
{
    public const string ParameterTokenPrefix = "@@PARAM:";
    public const string ParameterTokenSuffix = "@@";
    public const string MetricsFileName = "metrics.csv";
    private const string InputFileName = "model.dat";
    private const string SolverOutputFileName = "solver.out";
    private const string SolverJobName = "truss_case";
    private const long MaximumMetricsSize = 64 * 1024;
    private static readonly Regex ParameterNamePattern = new(@"^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex MetricNamePattern = new(@"^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex UnitPattern = new(@"^[A-Za-z0-9/%^*._-]{0,24}$", RegexOptions.CultureInvariant);
    private static readonly Regex TokenPattern = new(@"@@PARAM:([A-Z][A-Z0-9_]{0,63})@@", RegexOptions.CultureInvariant);
    private static readonly Regex SolverErrorPattern = new(
        @"^\s*(?:\*{3}\s*(?:ERROR|FATAL(?: ERROR)?)\s*\*{3}|NUMBER OF ERROR\s+MESSAGES ENCOUNTERED\s*=\s*[1-9]\d*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly ApdlBatchRunnerOptions _options;

    public ApdlBatchRunner(ApdlBatchRunnerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ApdlBatchRunResult> RunAsync(
        ApdlBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string caseDirectory = string.Empty;
        int? exitCode = null;
        var stage = ApdlBatchRunStage.Validation;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchExecutable = ValidateOptionsAndRequest(request);

            var templateBytes = await File.ReadAllBytesAsync(_options.ReviewedTemplatePath, cancellationToken);
            var actualHash = Convert.ToHexString(SHA256.HashData(templateBytes));
            if (!string.Equals(actualHash, NormalizeHash(_options.ReviewedTemplateSha256), StringComparison.Ordinal))
                throw new InvalidOperationException("APDL 模板指纹与已审阅版本不符；未运行求解器。");

            var sourceText = Encoding.Latin1.GetString(templateBytes);
            var materializedInput = BindNumericParameters(sourceText, request.NumericBindings);
            stage = ApdlBatchRunStage.WorkspaceCreation;
            caseDirectory = CreateUniqueCaseDirectory(request.CaseLabel);

            stage = ApdlBatchRunStage.InputPreparation;
            var inputPath = Path.Combine(caseDirectory, InputFileName);
            await File.WriteAllBytesAsync(inputPath, Encoding.Latin1.GetBytes(materializedInput), cancellationToken);

            stage = ApdlBatchRunStage.SolverExecution;
            using var process = new Process { StartInfo = BuildStartInfo(batchExecutable, caseDirectory, inputPath) };
            if (!process.Start())
                throw new InvalidOperationException("Mechanical APDL 启动失败，未能创建求解进程。");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                var exited = await StopAsync(process);
                if (!exited)
                    throw new InvalidOperationException("已发出停止请求，无法确认 APDL 进程退出。请检查本次算例进程。");
                if (cancellationToken.IsCancellationRequested)
                    throw;
                throw new TimeoutException($"APDL 求解超过时限 {_options.Timeout.TotalMinutes:0.#} 分钟，已终止求解进程。");
            }

            exitCode = process.ExitCode;
            var outputPath = Path.Combine(caseDirectory, SolverOutputFileName);
            if (exitCode == unchecked((int)0xC0000135))
                throw new InvalidOperationException("Mechanical APDL 启动时找不到运行库（0xC0000135）。请核对 ANSYS 安装和批处理启动程序。");
            if (exitCode != 0)
                throw new InvalidOperationException($"APDL 求解器退出码为 {exitCode}。");
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException("APDL 未生成 solver.out；求解结果状态无法确认。");

            await CheckSolverLogAsync(outputPath, requireCompletion: true, cancellationToken);
            var errorPath = Path.Combine(caseDirectory, SolverJobName + ".err");
            if (File.Exists(errorPath))
                await CheckSolverLogAsync(errorPath, requireCompletion: false, cancellationToken);

            stage = ApdlBatchRunStage.ResultParsing;
            var metricsPath = Path.Combine(caseDirectory, MetricsFileName);
            var metrics = await ReadMetricsAsync(metricsPath, request.RequiredMetricUnits, cancellationToken);
            var metricNames = metrics.Select(metric => metric.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingMetrics = request.RequiredMetrics.Where(name => !metricNames.Contains(name)).ToArray();
            if (missingMetrics.Length > 0)
                throw new InvalidOperationException($"APDL 结果缺少必需指标：{string.Join("、", missingMetrics)}。");

            stage = ApdlBatchRunStage.Completed;
            return new(true, stage, caseDirectory, exitCode, metrics,
                $"APDL 求解及结果读取完成；本次独立算例目录：{caseDirectory}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, stage, caseDirectory, exitCode, [],
                "APDL 求解已由用户取消；本次算例文件保留，未自动删除。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return new(false, stage, caseDirectory, exitCode, [],
                $"{StageLabel(stage)}失败：{ex.Message}" +
                (caseDirectory.Length == 0 ? string.Empty : $" 算例文件保留在：{caseDirectory}"));
        }
    }

    private string ValidateOptionsAndRequest(ApdlBatchRequest request)
    {
        var batchExecutable = ResolveBatchExecutable(_options.MechanicalApdlExecutablePath);

        if (!Path.IsPathFullyQualified(_options.ReviewedTemplatePath) || !File.Exists(_options.ReviewedTemplatePath))
            throw new FileNotFoundException("缺少已审阅的 APDL 输入模板。", _options.ReviewedTemplatePath);
        RejectReparsePoints(_options.ReviewedTemplatePath);
        var extension = Path.GetExtension(_options.ReviewedTemplatePath);
        if (!extension.Equals(".dat", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".inp", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mac", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("APDL 输入模板必须使用 .dat、.inp 或 .mac 扩展名。");
        _ = NormalizeHash(_options.ReviewedTemplateSha256);

        if (!Path.IsPathFullyQualified(_options.RunRootPath))
            throw new InvalidOperationException("APDL 算例根目录必须是完整路径。");
        var runRoot = Path.GetFullPath(_options.RunRootPath);
        if (_options.ProtectedPaths.Count == 0)
            throw new InvalidOperationException("必须配置受保护的源码、初始模型和工作模型路径，才能证明 APDL 输出目录隔离。");
        var protectedPaths = _options.ProtectedPaths
            .Append(AppContext.BaseDirectory)
            .Append(Path.GetDirectoryName(_options.MechanicalApdlExecutablePath)!)
            .Append(Path.GetDirectoryName(_options.ReviewedTemplatePath)!)
            .Select(NormalizeProtectedPath);
        if (protectedPaths.Any(path => PathsOverlap(runRoot, path)))
            throw new InvalidOperationException("APDL 算例根目录必须与源码、CAD 原件、CAD 工作模型及程序输出目录分离。");
        if (string.IsNullOrWhiteSpace(request.CaseLabel) ||
            !Regex.IsMatch(request.CaseLabel, @"^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("算例名称只能包含字母、数字、下划线和连字符，长度为 1 至 40。", nameof(request));
        if (_options.Timeout <= TimeSpan.Zero || _options.Timeout > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(_options), "求解时限必须大于零且不超过 24 小时。");

        if (request.NumericBindings is null)
            throw new ArgumentException("必须提供显式 APDL 数值绑定字典。", nameof(request));
        foreach (var pair in request.NumericBindings)
        {
            if (!ParameterNamePattern.IsMatch(pair.Key) || !double.IsFinite(pair.Value))
                throw new ArgumentException($"APDL 参数绑定无效：{pair.Key}", nameof(request));
        }

        if (request.RequiredMetrics is null || request.RequiredMetrics.Count == 0 ||
            request.RequiredMetrics.Any(name => !MetricNamePattern.IsMatch(name)))
            throw new ArgumentException("必需结果指标名称无效。", nameof(request));
        if (request.RequiredMetricUnits is null ||
            request.RequiredMetrics.Any(name => !request.RequiredMetricUnits.TryGetValue(name, out var unit) ||
                !UnitPattern.IsMatch(unit)) ||
            request.RequiredMetricUnits.Keys.Any(name => !request.RequiredMetrics.Contains(name)))
            throw new ArgumentException("每项必需结果都必须声明单位，且不能为未请求的指标声明单位。", nameof(request));

        return batchExecutable;
    }

    private static string ResolveBatchExecutable(string configuredPath)
    {
        if (!Path.IsPathFullyQualified(configuredPath) || !File.Exists(configuredPath))
            throw new FileNotFoundException("Mechanical APDL 求解器路径无效。", configuredPath);
        RejectReparsePoints(configuredPath);

        var directory = Path.GetDirectoryName(configuredPath)!;
        var enginePath = Path.Combine(directory, "ANSYS.exe");
        if (!File.Exists(enginePath))
            throw new FileNotFoundException("未找到安装目录中的 Mechanical APDL 引擎。", enginePath);
        RejectReparsePoints(enginePath);
        var engine = FileVersionInfo.GetVersionInfo(enginePath);
        var fileDescription = engine.FileDescription ?? "";
        if (!fileDescription.Contains("Mechanical APDL", StringComparison.OrdinalIgnoreCase) &&
            !fileDescription.Contains("ANSYS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安装目录中的 ANSYS.exe 无法验证为 Mechanical APDL。");

        // Resolve the versioned vendor wrapper across both legacy (18.0 -> ANSYS180.exe)
        // and modern year-based Ansys releases (2019 R1..2025 R1+ -> ANSYS193/212/232/242/251.exe or MAPDL.exe).
        var wrapperPath = ResolveVersionedBatchWrapper(directory, engine.FileVersion);
        var configuredName = Path.GetFileName(configuredPath);
        if (wrapperPath is not null)
        {
            var wrapperName = Path.GetFileName(wrapperPath);
            if (!string.Equals(configuredName, "ANSYS.exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(configuredName, wrapperName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(configuredName, "mapdl.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"批量求解须使用该安装目录的 {wrapperName}。");
            RejectReparsePoints(wrapperPath);
            return wrapperPath;
        }

        // Newer Ansys releases or slim installs where ANSYS.exe / mapdl.exe runs directly
        var mapdlPath = Path.Combine(directory, "mapdl.exe");
        if (File.Exists(mapdlPath))
        {
            RejectReparsePoints(mapdlPath);
            return mapdlPath;
        }

        return enginePath;
    }

    internal static string? ResolveVersionedBatchWrapper(string directory, string? fileVersion)
    {
        var candidates = new List<string>();

        // Legacy: "Mechanical APDL Release 18.0" -> ANSYS180.exe
        var legacyMatch = Regex.Match(fileVersion ?? "", @"Release\s+(\d{2})\.(\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (legacyMatch.Success)
        {
            candidates.Add($"ANSYS{legacyMatch.Groups[1].Value}{legacyMatch.Groups[2].Value}.exe");
        }

        // Modern: "2023 R2" / "2024 R2" / "2025 R1" -> ANSYS232.exe / ANSYS242.exe / ANSYS251.exe
        var modernMatch = Regex.Match(fileVersion ?? "", @"20(\d{2})\s*R(\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (modernMatch.Success)
        {
            candidates.Add($"ANSYS{modernMatch.Groups[1].Value}{modernMatch.Groups[2].Value}.exe");
        }

        // Directory path version folder fallback (e.g. ...\v180\ANSYS\bin\winx64 -> ANSYS180.exe, ...\v242\... -> ANSYS242.exe)
        var folderMatch = Regex.Match(directory, @"[\\/]v(\d{3})[\\/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (folderMatch.Success)
        {
            candidates.Add($"ANSYS{folderMatch.Groups[1].Value}.exe");
        }

        foreach (var name in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.Combine(directory, name);
            if (File.Exists(full)) return full;
        }

        // Direct directory scan for any ANSYS###.exe wrapper right alongside ANSYS.exe
        try
        {
            return Directory.EnumerateFiles(directory, "ANSYS*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(p => Regex.IsMatch(Path.GetFileName(p), @"^ANSYS\d{3}\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }
        catch
        {
            return null;
        }
    }

    private static string BindNumericParameters(string template, IReadOnlyDictionary<string, double> bindings)
    {
        var tokens = TokenPattern.Matches(template).Select(match => match.Groups[1].Value).ToArray();
        if (tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length)
            throw new InvalidOperationException("APDL 模板中参数占位符重复；每个分析参数必须有唯一的绑定位置。");

        var tokenSet = tokens.ToHashSet(StringComparer.Ordinal);
        var bindingSet = bindings.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = tokenSet.Except(bindingSet, StringComparer.Ordinal).ToArray();
        var unexpected = bindingSet.Except(tokenSet, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
            throw new InvalidOperationException(
                $"APDL 参数绑定与模板不一致。缺失：{FormatNames(missing)}；未使用：{FormatNames(unexpected)}。");

        var materialized = TokenPattern.Replace(template, match =>
            bindings[match.Groups[1].Value].ToString("G17", CultureInfo.InvariantCulture));
        if (materialized.Contains(ParameterTokenPrefix, StringComparison.Ordinal) ||
            materialized.Contains(ParameterTokenSuffix, StringComparison.Ordinal))
            throw new InvalidOperationException("APDL 输入仍含未解析的参数占位符。");
        return materialized;
    }

    private static ProcessStartInfo BuildStartInfo(string batchExecutable, string caseDirectory, string inputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = batchExecutable,
            WorkingDirectory = caseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("noread");
        startInfo.ArgumentList.Add("-np");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-j");
        startInfo.ArgumentList.Add(SolverJobName);
        startInfo.ArgumentList.Add("-dir");
        startInfo.ArgumentList.Add(caseDirectory);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(Path.Combine(caseDirectory, SolverOutputFileName));
        foreach (var name in startInfo.Environment.Keys.ToArray())
        {
            if (name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_PASSWORD", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase))
                startInfo.Environment.Remove(name);
        }
        return startInfo;
    }

    private string CreateUniqueCaseDirectory(string caseLabel)
    {
        var root = Path.GetFullPath(_options.RunRootPath);
        RejectReparsePoints(root);
        Directory.CreateDirectory(root);
        RejectReparsePoints(root);

        var name = $"{caseLabel}-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var caseDirectory = Path.Combine(root, name);
        if (Path.Exists(caseDirectory))
            throw new IOException("本次算例目录已存在。");
        Directory.CreateDirectory(caseDirectory);
        RejectReparsePoints(caseDirectory);
        return caseDirectory;
    }

    private static async Task CheckSolverLogAsync(string path, bool requireCompletion, CancellationToken cancellationToken)
    {
        RejectReparsePoints(path);
        using var reader = File.OpenText(path);
        var completed = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Trim().Trim('|').Trim().Equals("ANSYS RUN COMPLETED", StringComparison.OrdinalIgnoreCase))
                completed = true;
            if (!SolverErrorPattern.IsMatch(line))
                continue;
            var detail = await reader.ReadLineAsync(cancellationToken);
            if (detail is not null && detail.Length > 240)
                detail = detail[..240];
            throw new InvalidOperationException($"APDL 在 {Path.GetFileName(path)} 中报告错误。{detail?.Trim()}");
        }
        if (requireCompletion && !completed)
            throw new InvalidOperationException("APDL 输出缺少求解完成标记；结果状态无法确认。");
    }

    private static async Task<IReadOnlyList<ApdlMetric>> ReadMetricsAsync(
        string path,
        IReadOnlyDictionary<string, string> requiredUnits,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"APDL 未生成必需结果文件 {MetricsFileName}；结果不可用。");
        RejectReparsePoints(path);
        if (new FileInfo(path).Length > MaximumMetricsSize)
            throw new InvalidOperationException("APDL 指标文件超过 64 KB。");

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lines.Length < 2 || !string.Equals(lines[0].Trim(), "metric,value,unit", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("APDL 指标文件格式无效；要求首行为 metric,value,unit。");

        var metrics = new List<ApdlMetric>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
                continue;
            var columns = lines[index].Split(',');
            if (columns.Length != 3)
                throw new InvalidOperationException($"APDL 指标文件第 {index + 1} 行列数无效。");

            var name = columns[0].Trim();
            var rawValue = columns[1].Trim();
            var unit = columns[2].Trim();
            if (!MetricNamePattern.IsMatch(name) || !UnitPattern.IsMatch(unit) ||
                !double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value))
                throw new InvalidOperationException($"APDL 指标文件第 {index + 1} 行包含无效名称、单位或数值。");
            if (!seen.Add(name))
                throw new InvalidOperationException($"APDL 指标文件重复报告指标 {name}。");
            if (requiredUnits.TryGetValue(name, out var expectedUnit) &&
                !string.Equals(unit, expectedUnit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"APDL 指标 {name} 单位为 {unit}，预期单位为 {expectedUnit}；结果不接受。");
            metrics.Add(new(name, value, unit));
        }

        if (metrics.Count == 0)
            throw new InvalidOperationException("APDL 指标文件没有结果记录。");
        return metrics;
    }

    private static string NormalizeHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || !Regex.IsMatch(hash, @"^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("APDL 审阅模板必须配置完整的 SHA-256。", nameof(hash));
        return hash.ToUpperInvariant();
    }

    private static string FormatNames(IEnumerable<string> names)
    {
        var values = names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return values.Length == 0 ? "无" : string.Join("、", values);
    }

    private static string StageLabel(ApdlBatchRunStage stage) => stage switch
    {
        ApdlBatchRunStage.Validation => "预检",
        ApdlBatchRunStage.WorkspaceCreation => "创建隔离算例目录",
        ApdlBatchRunStage.InputPreparation => "准备 APDL 输入",
        ApdlBatchRunStage.SolverExecution => "启动或运行 ANSYS 求解器",
        ApdlBatchRunStage.ResultParsing => "读取 APDL 结果",
        _ => "APDL 算例"
    };

    private static bool PathsOverlap(string first, string second)
    {
        first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
               first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProtectedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("保护目录必须是明确的绝对路径。");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectReparsePoints(normalized);
        return normalized;
    }

    private static void RejectReparsePoints(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("路径必须是明确的绝对路径。");
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"拒绝经过链接或重解析点路径：{current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static async Task<bool> StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
