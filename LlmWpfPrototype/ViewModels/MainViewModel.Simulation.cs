using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Chat;
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

    private bool IsConfiguredAnsysLauncherAvailable()
    {
        return !string.IsNullOrWhiteSpace(ResolveEffectiveAnsysLauncherPath());
    }

    private bool IsConfiguredAnsysSolverAvailable()
    {
        return !string.IsNullOrWhiteSpace(ResolveEffectiveAnsysSolverPath());
    }

    private string ResolveEffectiveAnsysLauncherPath()
    {
        var configured = _simulationOptions.LauncherPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // If explicitly configured (including unit tests injecting fake paths), honor it strictly when valid.
            if (Path.IsPathFullyQualified(configured) &&
                File.Exists(configured) &&
                string.Equals(Path.GetFileName(configured), "launcher.exe", StringComparison.OrdinalIgnoreCase))
            {
                return configured;
            }

            // If explicitly set to an invalid/non-launcher path during tests, do NOT auto-substitute another PC binary
            // unless it looks like an old machine's missing launcher.exe.
            if (!string.Equals(Path.GetFileName(configured), "launcher.exe", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }

        var detected = AnsysInstallationDetector.DetectBestInstallation();
        if (detected is not null && !string.IsNullOrWhiteSpace(detected.LauncherPath))
        {
            _simulationOptions.LauncherPath = detected.LauncherPath;
            if (string.IsNullOrWhiteSpace(_simulationOptions.SolverPath) && !string.IsNullOrWhiteSpace(detected.SolverPath))
            {
                _simulationOptions.SolverPath = detected.SolverPath;
            }
            return detected.LauncherPath;
        }

        return string.Empty;
    }

    private string ResolveEffectiveAnsysSolverPath()
    {
        var configured = _simulationOptions.SolverPath;
        if (Path.IsPathFullyQualified(configured) && File.Exists(configured))
        {
            return configured;
        }

        var detected = AnsysInstallationDetector.DetectBestInstallation();
        if (detected is not null && !string.IsNullOrWhiteSpace(detected.SolverPath))
        {
            _simulationOptions.SolverPath = detected.SolverPath;
            return detected.SolverPath;
        }

        return string.Empty;
    }

    private bool IsConfiguredLightweightStudyAvailable()
    {
        if (string.IsNullOrWhiteSpace(_simulationOptions.StudyFile))
        {
            return false;
        }

        try
        {
            var studyFile = Path.GetFullPath(_simulationOptions.StudyFile, AppPaths.BundledConfigDirectory);
            return File.Exists(studyFile);
        }
        catch
        {
            return false;
        }
    }

    private async Task HandleSimulationChatAsync(
        string input,
        bool launcherSelectedByLlm = false,
        SimulationCommand? explicitCommand = null)
    {
        var command = explicitCommand ?? ParseSimulationCommand(input);
        try
        {
            if (command == SimulationCommand.OpenAnsys || launcherSelectedByLlm)
            {
                var started = StartConfiguredAnsysLauncher(out var launcherReply);
                _memoryStore.RecordActionTurn(
                    input,
                    launcherReply,
                    "open_apdl_launcher",
                    started ? "启动本地 ANSYS APDL 启动器" : "未找到配置的 ANSYS 启动器",
                    started,
                    ConversationTurnKind.SimulationAction);
                PersistActiveConversationSnapshot(input);
                return;
            }

            switch (command)
            {
                case SimulationCommand.ReadBaseline:
                    if (_solidWorksService is not SolidWorksService cad ||
                        (!_workspaceManager.HasOpenedWorkingModel && !IsActiveDocumentInCurrentProjectWorkspace()))
                    {
                        const string referenceReply = "轻量化对比参考尺寸为 70×70×5 mm（用户提供）。要读取实际尺寸，请先在程序中打开工作模型，再发送“读取轻量化基线”。";
                        AddAssistantMessage(referenceReply);
                        _memoryStore.RecordActionTurn(
                            input,
                            referenceReply,
                            "read_lightweight_baseline",
                            "当前未打开工作模型，返回轻量化参考尺寸提示",
                            succeeded: false,
                            ConversationTurnKind.SimulationAction);
                        PersistActiveConversationSnapshot(input);
                        return;
                    }

                    var baseline = await cad.ReadConfiguredLightweightBaselineAsync(
                        _workspaceManager.GetWorkingAssemblyPath(), _editableTrussMemberCatalogService.GetEnabledMembers().ToArray(),
                        message => AppendLog(message));
                    var baselineReply = FormatLightweightBaseline(baseline);
                    AddAssistantMessage(baselineReply);
                    _memoryStore.RecordActionTurn(
                        input,
                        baselineReply,
                        "read_lightweight_baseline",
                        baseline.AssemblyVerified ? "已读取工作模型轻量化基线尺寸" : "轻量化基线读取未通过完整校验",
                        baseline.AssemblyVerified,
                        ConversationTurnKind.SimulationAction);
                    PersistActiveConversationSnapshot(input);
                    return;

                case SimulationCommand.RunStudy:
                    if (string.IsNullOrWhiteSpace(_simulationOptions.StudyFile))
                    {
                        const string unconfiguredReply = "当前还没有配置这辆检查车的 APDL 算例。请先配置分析输入、材料、载荷、支承、对比尺寸和校核限值。现有的 APDL 调试模板不包含完整工况。";
                        AddAssistantMessage(unconfiguredReply);
                        _memoryStore.RecordActionTurn(
                            input,
                            unconfiguredReply,
                            "run_lightweight_study",
                            "未配置轻量化 APDL 算例文件，未启动求解",
                            succeeded: false,
                            ConversationTurnKind.SimulationAction);
                        PersistActiveConversationSnapshot(input);
                        return;
                    }

                    var studyFile = Path.GetFullPath(_simulationOptions.StudyFile, AppPaths.BundledConfigDirectory);
                    var runRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Sanliu", "ApdlRuns");
                    AddAssistantMessage("正在按算例中的截面和工况进行比较。每组求解都有独立的输入、日志和结果。");
                    var study = await LightweightStudy.RunAsync(studyFile, _simulationOptions.SolverPath, runRoot,
                        [AppPaths.ProjectRoot, _workspaceManager.InitialModelFolder, _workspaceManager.WorkingModelFolder]);
                    var studyReply = study.ToDisplayText();
                    AddAssistantMessage(studyReply);
                    var studySucceeded = string.IsNullOrWhiteSpace(study.Error) && study.Cases.Count > 0;
                    _memoryStore.RecordActionTurn(
                        input,
                        studyReply,
                        "run_lightweight_study",
                        studySucceeded ? $"完成轻量化仿真比较（{study.Cases.Count} 组算例）" : "轻量化仿真未完全成功",
                        studySucceeded,
                        ConversationTurnKind.SimulationAction);
                    PersistActiveConversationSnapshot(input);
                    return;
            }
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            AppendLog($"[APDL] {command} Failed: {exception.Message}");
            var failureReply = $"操作未完成。{exception.Message}";
            AddAssistantMessage(failureReply);
            _memoryStore.RecordActionTurn(
                input,
                failureReply,
                command.ToString(),
                $"仿真操作失败：{exception.Message}",
                succeeded: false,
                ConversationTurnKind.SimulationAction);
            PersistActiveConversationSnapshot(input);
        }
    }

    private bool StartConfiguredAnsysLauncher(out string assistantReply)
    {
        var path = ResolveEffectiveAnsysLauncherPath();
        if (string.IsNullOrWhiteSpace(path) && System.Windows.Application.Current?.MainWindow is { } owner)
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "未自动检测到本机 ANSYS APDL，请定位 launcher.exe（仅需选择一次）",
                Filter = "ANSYS APDL 启动器 (launcher.exe)|launcher.exe|可执行文件 (*.exe)|*.exe"
            };
            if (picker.ShowDialog(owner) == true &&
                File.Exists(picker.FileName) &&
                string.Equals(Path.GetFileName(picker.FileName), "launcher.exe", StringComparison.OrdinalIgnoreCase))
            {
                path = picker.FileName;
                _simulationOptions.LauncherPath = path;
                AnsysInstallationDetector.SaveUserPickedLauncherPath(path);
                AppendLog($"[APDL] 用户已手动定位本机 ANSYS 启动器并缓存：{path}");
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            AppendLog("[APDL] LauncherValidationFailed=True");
            assistantReply = "未在本机自动检测到 ANSYS APDL 安装目录（支持 ANSYS 18.0 ~ 2025 R1+）。请确认本机已安装 ANSYS Mechanical APDL。";
            AddAssistantMessage(assistantReply);
            return false;
        }

        _startAnsysLauncher(path);
        var versionTag = AnsysInstallationDetector.ExtractVersionTagFromPath(path);
        assistantReply = versionTag != "auto"
            ? $"已自动识别本机 ANSYS APDL（{versionTag}），启动器已启动。您可以在启动器中选择运行方式。"
            : "ANSYS APDL 启动器已启动。您可以在启动器中选择运行方式。";
        AddAssistantMessage(assistantReply);
        AppendLog($"[APDL] LauncherProcessStarted=True; Version={versionTag}");
        return true;
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
