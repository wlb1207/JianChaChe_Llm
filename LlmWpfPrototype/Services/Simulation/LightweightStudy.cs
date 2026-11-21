using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LlmWpfPrototype.Services.Simulation;

public sealed record LightweightStudySection(double WidthMm, double HeightMm, double WallMm)
{
    public override string ToString() =>
        FormattableString.Invariant($"{WidthMm:G8} × {HeightMm:G8} × {WallMm:G8} mm");
}

public sealed record LightweightStudyCase(
    string Id, LightweightStudySection Section, string Directory, string? Error,
    double? MassKg = null, double? MaxStressMpa = null, double? MaxDisplacementMm = null,
    double? MassReductionPercent = null, bool? WithinSpecifiedLimits = null);

public sealed record LightweightStudyResult(
    string LoadCase, double StressLimitMpa, double DisplacementLimitMm,
    IReadOnlyList<LightweightStudyCase> Cases, string? Error)
{
    public string Material { get; init; } = "";
    public string Supports { get; init; } = "";
    public string Loads { get; init; } = "";

    public string ToDisplayText()
    {
        if (Cases.Count == 0)
            return Error ?? "没有可报告的仿真结果。";

        var text = new StringBuilder();
        text.AppendLine($"轻量化比较 | {LoadCase}");
        text.AppendLine($"基线与候选采用同一材料（{Material}）、支承（{Supports}）和载荷（{Loads}）。");
        text.AppendLine(FormattableString.Invariant(
            $"本工况限值：等效应力 ≤ {StressLimitMpa:G8} MPa，总位移 ≤ {DisplacementLimitMm:G8} mm。"));
        foreach (var result in Cases)
        {
            string label = result.Id == "baseline" ? "基线" : $"候选 {result.Id}";
            text.Append($"{label}（{result.Section}）");
            if (result.Error is not null)
            {
                text.AppendLine($"：{result.Error}");
            }
            else
            {
                text.Append(FormattableString.Invariant(
                    $"：质量 {result.MassKg:G8} kg，最大等效应力 {result.MaxStressMpa:G8} MPa，最大总位移 {result.MaxDisplacementMm:G8} mm"));
                if (result.MassReductionPercent is { } reduction)
                {
                    text.Append(reduction > 0
                        ? FormattableString.Invariant($"，相对基线减重 {reduction:G6}%")
                        : "，未实现减重");
                }
                text.AppendLine(result.WithinSpecifiedLimits == true ? "。两项结果在本工况限值内。" : "。至少一项超出本工况限值。");
            }
            if (!string.IsNullOrEmpty(result.Directory))
                text.AppendLine($"  求解目录：{result.Directory}");
        }
        if (Error is not null)
            text.AppendLine(Error);
        text.Append("结果仅覆盖此工况及上述两个限值。");
        if (Cases.Any(item => item.MassReductionPercent > 0 && item.WithinSpecifiedLimits == true))
            text.Append("如选定待调整的截面，可另行提交尺寸修改并确认。");
        return text.ToString();
    }
}

// Applies the same reviewed deck and fixed parameters to every shape in one configured load case.
public static class LightweightStudy
{
    private const string MassMetric = "mass_kg";
    private const string StressMetric = "max_von_mises_mpa";
    private const string DisplacementMetric = "max_total_displacement_mm";
    private static readonly IReadOnlyDictionary<string, string> MetricUnits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [MassMetric] = "kg",
        [StressMetric] = "MPa",
        [DisplacementMetric] = "mm"
    };
    private static readonly IReadOnlySet<string> RequiredMetrics = MetricUnits.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex ParameterName = new(@"\A[A-Z][A-Z0-9_]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex CaseId = new(@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,39}\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<LightweightStudyResult> RunAsync(
        string studyJsonPath, string solverPath, string runRoot,
        IReadOnlyList<string> protectedPaths, CancellationToken ct = default)
    {
        var results = new List<LightweightStudyCase>();
        StudyConfiguration? study = null;
        try
        {
            if (protectedPaths is null || protectedPaths.Count == 0 ||
                protectedPaths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
                throw new InvalidOperationException("必须提供源码、初始 CAD 和工作 CAD 的完整保护路径，才能运行轻量化比较。");
            if (string.IsNullOrWhiteSpace(studyJsonPath) || !File.Exists(studyJsonPath))
                throw new InvalidOperationException("尚未配置检查车仿真算例。需要已核对的参数化 APDL 输入、基线和候选截面、材料、支承、载荷工况及验收限值。");

            var studyPath = Path.GetFullPath(studyJsonPath);
            using var file = File.OpenRead(studyPath);
            study = await JsonSerializer.DeserializeAsync<StudyConfiguration>(file, JsonOptions, ct)
                ?? throw new InvalidOperationException("仿真算例配置为空。");
            var templatePath = Validate(study, studyPath);
            var runner = new ApdlBatchRunner(new ApdlBatchRunnerOptions(solverPath, templatePath,
                study.TemplateSha256, runRoot, TimeSpan.FromSeconds(study.TimeoutSeconds))
            {
                ProtectedPaths = protectedPaths
            });
            var bindingNames = study.SectionParameters!;
            var baseline = await SolveCaseAsync("baseline", study.Baseline!,
                runner, study, bindingNames, baselineMass: null, ct);
            results.Add(baseline);
            ct.ThrowIfCancellationRequested();
            if (baseline.Error is not null)
                return Result("基线求解或结果校验失败，候选比较已中止。");

            foreach (var candidate in study.Candidates!)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await SolveCaseAsync(candidate.Id, candidate.Section, runner, study,
                    bindingNames, baseline.MassKg, ct));
                ct.ThrowIfCancellationRequested();
            }
            return Result(error: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result("仿真比较已取消，已有算例目录仍可查看。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or ArgumentException or InvalidOperationException)
        {
            return Result($"仿真算例无法运行。{ex.Message}");
        }

        LightweightStudyResult Result(string? error) =>
            new(study?.LoadCase ?? "", study?.Limits?.MaxStressMpa ?? 0,
                study?.Limits?.MaxDisplacementMm ?? 0, results.ToArray(), error)
            {
                Material = study?.Material ?? "",
                Supports = study?.Supports ?? "",
                Loads = study?.Loads ?? ""
            };
    }

    private static async Task<LightweightStudyCase> SolveCaseAsync(
        string id, LightweightStudySection section, ApdlBatchRunner runner,
        StudyConfiguration study, SectionParameters parameterNames, double? baselineMass, CancellationToken ct)
    {
        var bindings = new Dictionary<string, double>(study.FixedParameters ?? [], StringComparer.Ordinal)
        {
            [parameterNames.Width] = section.WidthMm,
            [parameterNames.Height] = section.HeightMm,
            [parameterNames.Wall] = section.WallMm
        };

        var run = await runner.RunAsync(new ApdlBatchRequest(id, bindings, RequiredMetrics)
        {
            RequiredMetricUnits = MetricUnits
        }, ct);
        if (!run.Succeeded)
            return new LightweightStudyCase(id, section, run.CaseDirectory, run.Message);

        try
        {
            var mass = ReadMetric(run, MassMetric);
            var stress = ReadMetric(run, StressMetric);
            var displacement = ReadMetric(run, DisplacementMetric);
            if (mass <= 0)
                throw new InvalidOperationException("质量必须大于零。");
            double? reduction = baselineMass is { } original ? (1 - mass / original) * 100 : null;
            if (reduction is { } calculated && !double.IsFinite(calculated))
                throw new InvalidOperationException("与基线的质量比超出可表示范围。");
            return new LightweightStudyCase(id, section, run.CaseDirectory, null,
                mass, stress, displacement, reduction,
                stress <= study.Limits!.MaxStressMpa && displacement <= study.Limits.MaxDisplacementMm);
        }
        catch (InvalidOperationException ex)
        {
            return new LightweightStudyCase(id, section, run.CaseDirectory, $"求解指标校验失败。{ex.Message}");
        }
    }

    private static double ReadMetric(ApdlBatchRunResult run, string name)
    {
        var unit = MetricUnits[name];
        var metric = run.Metrics.SingleOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (metric is null || !string.Equals(metric.Unit, unit, StringComparison.Ordinal) ||
            !double.IsFinite(metric.Value) || metric.Value < 0)
            throw new InvalidOperationException($"{name} 必须为有效的非负数，单位必须为 {unit}。");
        return metric.Value;
    }

    private static string Validate(StudyConfiguration study, string studyPath)
    {
        if (string.IsNullOrWhiteSpace(study.LoadCase) || string.IsNullOrWhiteSpace(study.Material) ||
            string.IsNullOrWhiteSpace(study.Supports) || string.IsNullOrWhiteSpace(study.Loads))
            throw new InvalidOperationException("请在算例中明确材料、支承、载荷和工况。");

        if (string.IsNullOrWhiteSpace(study.TemplateFile) ||
            !string.Equals(Path.GetFileName(study.TemplateFile), study.TemplateFile, StringComparison.Ordinal) ||
            !(Path.GetExtension(study.TemplateFile).Equals(".dat", StringComparison.OrdinalIgnoreCase) ||
              Path.GetExtension(study.TemplateFile).Equals(".inp", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("templateFile 必须是与算例配置同目录的 APDL .dat 或 .inp 文件名。");
        if (study.TemplateSha256 is null || study.TemplateSha256.Length != 64 || !study.TemplateSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("请提供已核对 APDL 模板的 SHA-256。");
        if (study.TimeoutSeconds is <= 0 or > 86400)
            throw new InvalidOperationException("timeoutSeconds 必须为 1 至 86400 秒。");

        var names = study.SectionParameters;
        if (names is null)
            throw new InvalidOperationException("请提供宽、高和壁厚对应的三个 APDL 参数名。");
        string[] variableNames = [names.Width, names.Height, names.Wall];
        if (variableNames.Any(name => string.IsNullOrEmpty(name) || !ParameterName.IsMatch(name)) ||
            variableNames.Distinct(StringComparer.Ordinal).Count() != variableNames.Length)
            throw new InvalidOperationException("截面 APDL 参数名必须唯一，由大写字母开头并仅含大写字母、数字、下划线。");
        if (study.FixedParameters is not null && study.FixedParameters.Any(item =>
                !ParameterName.IsMatch(item.Key) || !double.IsFinite(item.Value) ||
                variableNames.Contains(item.Key, StringComparer.Ordinal)))
            throw new InvalidOperationException("固定参数必须使用有效的 APDL 名称和有限数值，并且不能覆盖截面参数。");

        if (study.Limits is not { } limits || !Positive(limits.MaxStressMpa) || !Positive(limits.MaxDisplacementMm))
            throw new InvalidOperationException("请提供正数的应力限值（MPa）和位移限值（mm）。");
        if (!ValidSection(study.Baseline))
            throw new InvalidOperationException("基线宽、高、壁厚必须为正数，截面内腔宽、高必须大于零。");
        if (study.Candidates is not { Length: > 0 })
            throw new InvalidOperationException("请至少配置一个待比较的候选截面。");

        var baseline = study.Baseline!;
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "baseline" };
        var shapes = new HashSet<LightweightStudySection>();
        foreach (var candidate in study.Candidates)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Id) ||
                !CaseId.IsMatch(candidate.Id) || !labels.Add(candidate.Id))
                throw new InvalidOperationException("候选 ID 必须唯一，使用字母、数字、下划线或连字符，且不得使用 baseline。");
            var section = candidate.Section;
            if (!ValidSection(section) || section.WidthMm > baseline.WidthMm || section.HeightMm > baseline.HeightMm ||
                section.WallMm > baseline.WallMm || section == baseline || !shapes.Add(section))
                throw new InvalidOperationException($"候选 {candidate.Id} 必须是规格不重复、内腔有效且至少一项尺寸小于基线的截面。");
        }
        return Path.Combine(Path.GetDirectoryName(studyPath)!, study.TemplateFile);
    }

    private static bool ValidSection(LightweightStudySection? section) =>
        section is { } value && Positive(value.WidthMm) && Positive(value.HeightMm) &&
        Positive(value.WallMm) && value.WallMm < Math.Min(value.WidthMm, value.HeightMm) / 2;

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private sealed record SectionParameters(string Width, string Height, string Wall);
    private sealed record EvaluationLimits(double MaxStressMpa, double MaxDisplacementMm);
    private sealed record Candidate(string Id, double WidthMm, double HeightMm, double WallMm)
    {
        public LightweightStudySection Section => new(WidthMm, HeightMm, WallMm);
    }
    private sealed record StudyConfiguration(
        string TemplateFile, string TemplateSha256, string LoadCase, string Material, string Supports, string Loads,
        SectionParameters? SectionParameters, Dictionary<string, double>? FixedParameters,
        LightweightStudySection? Baseline, Candidate[]? Candidates, EvaluationLimits? Limits, int TimeoutSeconds = 180);
}
