using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Simulation;

namespace LlmWpfPrototype.ViewModels;

public sealed partial class MainViewModel
{
    private readonly SimulationOptions _simulationOptions;
    private readonly Action<string> _startAnsysLauncher;

    private enum SimulationCommand { None, OpenAnsys, ReadBaseline, RunStudy }

    private static SimulationCommand ParseSimulationCommand(string text) =>
        NormalizeIntentText(text.Trim()).TrimEnd('。', '！', '.', '!') switch
        {
            "打开ansysapdl" or "启动ansysapdl" or "请打开ansysapdl" or "帮我打开ansysapdl"
                or "打开ansys" or "打开apdl" or "启动ansys" => SimulationCommand.OpenAnsys,
            "读取轻量化基线" or "查看轻量化基线" or "查询轻量化基线" => SimulationCommand.ReadBaseline,
            "运行轻量化仿真" or "运行轻量化分析" => SimulationCommand.RunStudy,
            _ => SimulationCommand.None
        };

    private static bool IsSimulationChatCommand(string text) => ParseSimulationCommand(text) != SimulationCommand.None;

    private static bool IsSimulationEntryInput(string text) =>
        IsSimulationChatCommand(text) || ApdlLaunchIntentPolicy.IsCandidate(text);

    private async Task HandleSimulationChatAsync(string input, bool launcherSelectedByLlm = false)
    {
        var command = ParseSimulationCommand(input);
        try
        {
            if (command == SimulationCommand.OpenAnsys || launcherSelectedByLlm)
            {
                StartConfiguredAnsysLauncher();
                return;
            }

            switch (command)
            {
                case SimulationCommand.ReadBaseline:
                    if (!_workspaceManager.HasOpenedWorkingModel || _solidWorksService is not SolidWorksService cad)
                    {
                        AddAssistantMessage("轻量化对比参考尺寸为 70×70×5 mm（用户提供）。要读取实际尺寸，请先在程序中打开工作模型，再发送“读取轻量化基线”。");
                        return;
                    }

                    var baseline = await cad.ReadConfiguredLightweightBaselineAsync(
                        _workspaceManager.GetWorkingAssemblyPath(), _editableTrussMemberCatalogService.GetEnabledMembers().ToArray(),
                        message => AppendLog(message));
                    AddAssistantMessage(FormatLightweightBaseline(baseline));
                    return;

                case SimulationCommand.RunStudy:
                    if (string.IsNullOrWhiteSpace(_simulationOptions.StudyFile))
                    {
                        AddAssistantMessage("当前还没有配置这辆检查车的 APDL 算例。请先配置分析输入、材料、载荷、支承、对比尺寸和校核限值。现有的 APDL 调试模板不包含完整工况。");
                        return;
                    }

                    var studyFile = Path.GetFullPath(_simulationOptions.StudyFile, AppPaths.BundledConfigDirectory);
                    var runRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Sanliu", "ApdlRuns");
                    AddAssistantMessage("正在按算例中的截面和工况进行比较。每组求解都有独立的输入、日志和结果。");
                    var study = await LightweightStudy.RunAsync(studyFile, _simulationOptions.SolverPath, runRoot,
                        [AppPaths.ProjectRoot, _workspaceManager.InitialModelFolder, _workspaceManager.WorkingModelFolder]);
                    AddAssistantMessage(study.ToDisplayText());
                    return;
            }
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            AppendLog($"[APDL] {command} Failed: {exception.Message}");
            AddAssistantMessage($"操作未完成。{exception.Message}");
        }
    }

    private void StartConfiguredAnsysLauncher()
    {
        var path = _simulationOptions.LauncherPath;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !string.Equals(Path.GetFileName(path), "launcher.exe", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog("[APDL] LauncherValidationFailed=True");
            AddAssistantMessage("未找到 ANSYS 启动器。请检查程序配置中的 Simulation.LauncherPath。");
            return;
        }

        _startAnsysLauncher(path);
        AddAssistantMessage("ANSYS APDL 启动器已启动。你可以在启动器中选择运行方式。");
        AppendLog("[APDL] LauncherProcessStarted=True");
    }

    private static string FormatLightweightBaseline(LightweightBaselineResult baseline)
    {
        const string reference = "轻量化对比参考尺寸 70×70×5 mm（用户提供）。";
        if (!baseline.AssemblyVerified)
            return $"{reference}\n{string.Join("\n", baseline.Issues)}";

        var text = new StringBuilder(reference)
            .AppendLine().AppendLine($"已读取工作装配 {Path.GetFileName(baseline.AssemblyPath)}，配置 {baseline.AssemblyConfiguration}。");
        foreach (var member in baseline.Members)
        {
            text.AppendLine()
                .AppendLine($"{member.MemberName} · {FirstReported(member.ComponentName, member.ExpectedComponentName, Path.GetFileName(member.PartPath))}");
            if (!string.IsNullOrWhiteSpace(member.Configuration))
                text.AppendLine($"引用配置 {member.Configuration}。");
            text.AppendLine($"外宽 {Mm(member.OuterWidth.ValueMm)}，外高 {Mm(member.OuterHeight.ValueMm)}，壁厚 {Mm(member.ConsistentWallThicknessMm)}，杆长 {Mm(member.MemberLength.ValueMm)}。");
            if (member.ConsistentWallThicknessMm is null && member.ConfiguredThickness.ValueMm is decimal configuredWall)
                text.AppendLine($"映射的壁厚尺寸读数 {Mm(configuredWall)}，待核对。");
            if (member.MassKg is decimal measuredMass)
                text.AppendLine($"该实例的 CAD 质量 {measuredMass.ToString("0.###", CultureInfo.InvariantCulture)} kg。");
            if (!string.IsNullOrWhiteSpace(member.Material))
                text.AppendLine($"CAD 材料 {member.Material}。");
            if (member.HasUnsavedChanges == true)
                text.AppendLine("当前读数来自含未保存更改的文档。");
            foreach (var note in new[] { member.MappingNote,
                member.OuterWidth.ValueMm is null ? member.OuterWidth.Detail : "",
                member.OuterHeight.ValueMm is null ? member.OuterHeight.Detail : "",
                member.MemberLength.ValueMm is null ? member.MemberLength.Detail : "" }.Distinct())
                if (!string.IsNullOrWhiteSpace(note)) text.AppendLine(note);
        }

        if (baseline.MappedMembersMassKg is decimal mass)
            text.AppendLine().AppendLine($"已配置弦杆的 CAD 质量合计 {mass.ToString("0.###", CultureInfo.InvariantCulture)} kg。");
        foreach (var issue in baseline.Issues) text.AppendLine(issue);
        return text.ToString().TrimEnd();

        static string Mm(decimal? value) => value is decimal amount
            ? $"{amount.ToString("0.###", CultureInfo.InvariantCulture)} mm" : "待核实";
        static string FirstReported(params string[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "构件未确认";
    }

    private void StartAnsysLauncher(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            UseShellExecute = false
        };
        startInfo.Environment.Remove("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(_options.ApiKeyEnvName))
            startInfo.Environment.Remove(_options.ApiKeyEnvName);
        using var process = Process.Start(startInfo);

        if (process is null)
            throw new InvalidOperationException("无法创建 ANSYS 启动器进程。");
    }
}
