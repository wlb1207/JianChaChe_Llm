using System.Diagnostics;
using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Presentation;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Design;
using LlmWpfPrototype.Services.Rag;

namespace LlmWpfPrototype.ViewModels;

public sealed record WelcomeMemberBrief(
    string RoleTitle,
    string SpecText,
    string StatusBadgeText,
    bool HasWarning,
    string Line1,
    string Line2,
    string StatusBadgeTooltip = "");

public sealed record WelcomeScenarioCardItem(
    string Tag,
    string TagBackgroundHex,
    string TagForegroundHex,
    string Title,
    string Description,
    string Prompt,
    string ActionCue);

public sealed record WelcomeDashboardSnapshot(
    string ProjectName,
    string VersionTag,
    string ModelCodeBadge,
    string VehicleSubtitle,
    string FollowUpNote,
    bool HasFollowUpNote,
    string KbStatusText,
    bool IsKbReady,
    string SolidWorksStatusText,
    bool IsSolidWorksOpened,
    string ApdlStatusText,
    bool IsApdlReady,
    WelcomeMemberBrief UpperChord,
    WelcomeMemberBrief LowerChord,
    IReadOnlyList<string> RedlineItems,
    IReadOnlyList<WelcomeScenarioCardItem> ScenarioCards,
    bool IsApdlRunning = false,
    string KbStatusTooltip = "",
    string SolidWorksStatusTooltip = "",
    string ApdlStatusTooltip = "",
    string KnowledgeFigureGalleryButtonText = "规范图库");

public sealed partial class MainViewModel
{
    private WelcomeDashboardSnapshot? _welcomeDashboard;
    private bool _isChatBriefingExpanded;
    private RelayCommand? _toggleChatBriefingCommand;
    private RelayCommand? _previewVehicleLinkageDiagramCommand;
    private RelayCommand? _previewVehicleDeflectionDiagramCommand;
    private RelayCommand? _openKnowledgeFigureGalleryCommand;

    public bool ShowWelcomeDashboard =>
        HasActiveProject &&
        !HasPendingCadConfirmation &&
        ChatMessages.Count <= 1 &&
        ChatMessages.All(m => !m.IsUser);

    public bool ShowChatMessageStream =>
        HasActiveProject && !ShowWelcomeDashboard;

    public bool ShowCompactChatContextBar => ShowChatMessageStream;

    public bool IsChatBriefingExpanded
    {
        get => _isChatBriefingExpanded;
        set
        {
            if (SetProperty(ref _isChatBriefingExpanded, value))
            {
                OnPropertyChanged(nameof(ShowExpandedInlineBriefing));
                OnPropertyChanged(nameof(BriefingToggleButtonText));
            }
        }
    }

    public bool ShowExpandedInlineBriefing => ShowChatMessageStream && IsChatBriefingExpanded;

    public string BriefingToggleButtonText => IsChatBriefingExpanded ? "收起设计简报 ▴" : "展开设计简报 ▾";

    public RelayCommand ToggleChatBriefingCommand => _toggleChatBriefingCommand ??= new RelayCommand(() =>
    {
        if (!IsChatBriefingExpanded)
        {
            _welcomeDashboard = BuildWelcomeDashboardSnapshot();
            OnPropertyChanged(nameof(WelcomeDashboard));
        }

        IsChatBriefingExpanded = !IsChatBriefingExpanded;
    });

    public RelayCommand PreviewVehicleLinkageDiagramCommand => _previewVehicleLinkageDiagramCommand ??= new RelayCommand(() =>
    {
        RagFormattedText.OpenLocalKnowledgeFigurePreview(
            "本车 JC-GD-NG 主桁架弦杆截面与横向红管补偿联动断面图",
            "JC_GD_NG/assets/fig_jc_gd_ng_chord_linkage.png");
    });

    public RelayCommand PreviewVehicleDeflectionDiagramCommand => _previewVehicleDeflectionDiagramCommand ??= new RelayCommand(() =>
    {
        RagFormattedText.OpenLocalKnowledgeFigurePreview(
            "本车 JC-GD-NG 总体构型、悬臂静挠度与静动载试验总图",
            "JT_T_1430_2022/assets/fig_jc_gd_ng_deflection_and_test.png");
    });

    public RelayCommand OpenKnowledgeFigureGalleryCommand => _openKnowledgeFigureGalleryCommand ??= new RelayCommand(() =>
    {
        var figures = LocalKnowledgeSearchService.CreateForApplication().DiscoverAllEmbeddedFigures();
        if (figures.Count > 0)
        {
            RagFormattedText.OpenKnowledgeFigureGalleryWindow(figures);
        }
    });

    public string CompactChatContextText
    {
        get
        {
            var dash = WelcomeDashboard;
            var chordSummary = string.Equals(dash.UpperChord.SpecText, dash.LowerChord.SpecText, StringComparison.Ordinal)
                ? $"上/下弦 {dash.UpperChord.SpecText}"
                : $"上 {dash.UpperChord.SpecText} / 下 {dash.LowerChord.SpecText}";
            return $"{dash.ModelCodeBadge}（{dash.ProjectName} · {dash.VersionTag}） · {chordSummary}";
        }
    }

    public string CompactChatContextTooltip
    {
        get
        {
            var dash = WelcomeDashboard;
            return $"{dash.ModelCodeBadge}（{dash.ProjectName} · {dash.VersionTag}） · 上弦杆 {dash.UpperChord.SpecText} / 下弦杆 {dash.LowerChord.SpecText} · {dash.KbStatusText}";
        }
    }

    public WelcomeDashboardSnapshot WelcomeDashboard =>
        _welcomeDashboard ??= BuildWelcomeDashboardSnapshot();

    private void AddWelcomeAnchorMessage(string text)
    {
        ChatMessages.Add(new ChatMessage
        {
            IsUser = false,
            RoleDisplay = "系统",
            Text = text,
            IsSystemWelcomeAnchor = true
        });
    }

    private void RefreshWelcomeDashboard()
    {
        _welcomeDashboard = BuildWelcomeDashboardSnapshot();
        OnPropertyChanged(nameof(WelcomeDashboard));
        OnPropertyChanged(nameof(CompactChatContextText));
        OnPropertyChanged(nameof(CompactChatContextTooltip));
        OnPropertyChanged(nameof(ShowWelcomeDashboard));
        OnPropertyChanged(nameof(ShowChatMessageStream));
        OnPropertyChanged(nameof(ShowCompactChatContextBar));
        OnPropertyChanged(nameof(ShowExpandedInlineBriefing));
        OnPropertyChanged(nameof(ShowWelcomeQuickPrompts));
    }

    private void NotifyWelcomeVisibilityChanged()
    {
        var showDashboard = ShowWelcomeDashboard;
        if (showDashboard)
        {
            _welcomeDashboard = BuildWelcomeDashboardSnapshot();
            OnPropertyChanged(nameof(WelcomeDashboard));
        }

        OnPropertyChanged(nameof(CompactChatContextText));
        OnPropertyChanged(nameof(CompactChatContextTooltip));
        OnPropertyChanged(nameof(ShowWelcomeDashboard));
        OnPropertyChanged(nameof(ShowChatMessageStream));
        OnPropertyChanged(nameof(ShowCompactChatContextBar));
        OnPropertyChanged(nameof(ShowExpandedInlineBriefing));
        OnPropertyChanged(nameof(ShowWelcomeQuickPrompts));
    }

    private WelcomeDashboardSnapshot BuildWelcomeDashboardSnapshot()
    {
        var profile = DesignBoundaryProfileLoader.Load();
        var v = profile.Vehicle;
        var g3811 = profile.GbT3811;
        var g50017 = profile.Gb50017;
        var jtt = profile.JtT1430;

        var active = ActiveProject;
        var projectName = active?.Name ?? "当前设计项目";
        var versionTag = active?.VersionTag ?? "V0";
        var upper = active?.UpperChord ?? MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆");
        var lower = active?.LowerChord ?? MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆");

        var upperEval = DesignBoundaryEvaluationService.EvaluateMemberSection(
            upper.WidthMm, upper.HeightMm, upper.ThicknessMm, DesignBoundaryMemberRole.UpperChord, upper, profile);
        var lowerEval = DesignBoundaryEvaluationService.EvaluateMemberSection(
            lower.WidthMm, lower.HeightMm, lower.ThicknessMm, DesignBoundaryMemberRole.LowerChord, lower, profile);

        var upperBrief = BuildMemberBrief(
            "上弦杆 · 斜桁架受拉主弦",
            upper,
            upperEval,
            isUpper: true,
            profile);
        var lowerBrief = BuildMemberBrief(
            "下弦杆 · 斜桁架受压主弦",
            lower,
            lowerEval,
            isUpper: false,
            profile);

        var swDiag = _solidWorksService.GetSessionDiagnostics();
        var isModelOpened = swDiag.IsWorkingModelReady;
        var swStatusText = swDiag.State switch
        {
            SolidWorksSessionState.ConnectedWorkingAssembly => "SolidWorks 已打开",
            SolidWorksSessionState.ConnectedDifferentDocument when swDiag.HasOpenWorkingAssemblyInSession => "SolidWorks 已打开",
            SolidWorksSessionState.ConnectedDifferentDocument => "SolidWorks 已打开其他模型",
            SolidWorksSessionState.ProcessRunningComUnavailable => "SolidWorks 已运行 · COM 待重连",
            SolidWorksSessionState.ConnectedNoActiveDocument => "SolidWorks 已打开 · 未加载模型",
            _ => "SolidWorks 未打开"
        };
        var swStatusTooltip = swDiag.State switch
        {
            SolidWorksSessionState.ConnectedWorkingAssembly => $"已连接当前项目工作总装配：{swDiag.ActiveDocumentName}",
            SolidWorksSessionState.ConnectedDifferentDocument when swDiag.HasOpenWorkingAssemblyInSession => "本项目工作总装配已在 SolidWorks 后台打开，执行改模时会自动切回。",
            SolidWorksSessionState.ConnectedDifferentDocument => $"SolidWorks 当前打开的是其他目录模型（{swDiag.ActiveDocumentName}），发送“打开当前模型”即可切换至本项目副本。",
            SolidWorksSessionState.ProcessRunningComUnavailable => swDiag.DetailMessage,
            SolidWorksSessionState.ConnectedNoActiveDocument => "SolidWorks 进程已启动，尚未打开本项目工作总装配；发送“打开当前模型”即可加载。",
            _ => "SolidWorks 当前未启动（工作区已配置就绪，发送“打开当前模型”即可自动启动并打开本项目副本）。"
        };

        var isApdlConfigured = IsConfiguredAnsysLauncherAvailable();
        var isApdlRunning = IsAnsysApdlProcessRunning();
        var apdlStatusText = isApdlRunning
            ? "ANSYS APDL 运行中"
            : isApdlConfigured
                ? "ANSYS APDL 未打开"
                : "ANSYS APDL 未配置";
        var apdlStatusTooltip = isApdlRunning
            ? "检测到本机 ANSYS / APDL 进程正在运行。"
            : isApdlConfigured
                ? "本机已配置 ANSYS APDL 启动器与批处理求解器，当前未启动；发送“打开 APDL”即可打开。"
                : "尚未在配置中找到可用的 ANSYS APDL 启动器路径。";

        var kbSearchService = LocalKnowledgeSearchService.CreateForApplication();
        var kbSummary = kbSearchService.BuildKnowledgeBaseCatalogSummary();
        var isKbReady = !kbSummary.StartsWith("未检测到", StringComparison.Ordinal);
        var kbBadgeText = FormatCompactKbBadge(kbSummary, isKbReady);

        var cantileverLimit = Math.Round(v.CantileverLengthMm / jtt.SteelCantileverDeflectionDivisor, 1, MidpointRounding.AwayFromZero);
        var allowableComboA = DesignBoundaryEvaluationService.ComputeAllowableStressMpa(v, g3811, g3811.SafetyFactorComboA);
        var epsKRaw = (decimal)Math.Sqrt((double)(g50017.EpsilonKReferenceFyMpa / Math.Max(1m, v.YieldStrengthMpa)));
        var axialRatioLimit = Math.Round(g50017.AxialCompressionLocalStabilityFactor * epsKRaw, 1, MidpointRounding.AwayFromZero);

        var redlines = new[]
        {
            $"悬臂挠度 ≤ {cantileverLimit:0.#} mm（{jtt.StandardCode} 表3 L'/{jtt.SteelCantileverDeflectionDivisor}）",
            $"{v.MaterialGrade} 许用应力 ≈ {allowableComboA:0.#} MPa（{g3811.StandardCode} 表22 组合A）",
            $"闭口最小壁厚 ≥ {g3811.ClosedSectionMinWallModerateMm:0.#} mm（{g3811.StandardCode} 表34 中等腐蚀）",
            $"局部稳定 b₀/t ≤ {axialRatioLimit:0.#}（{g50017.StandardCode} 7.3.1 40εk）"
        };

        var hasFollowUpNote = active is not null && (active.CurrentVersionNumber > 0 || active.Conversations.Count > 1);
        var followUpNote = string.Empty;
        if (active is not null && active.CurrentVersionNumber > 0)
        {
            var latestSummary = active.VersionHistory.LastOrDefault()?.Summary;
            followUpNote = string.IsNullOrWhiteSpace(latestSummary)
                ? $"当前项目已迭代至 {versionTag}（上弦杆 {upper.SpecText} mm · 下弦杆 {lower.SpecText} mm），可继续优化截面或进入仿真复核。"
                : $"当前项目已迭代至 {versionTag}（最近记录：{latestSummary}），已自动继承最新三维截面与规范边界状态。";
        }
        else if (active is not null && active.Conversations.Count > 1)
        {
            followUpNote = $"已在项目【{projectName}】下开启新跟进对话，当前模型与项目记忆已自动衔接。";
        }

        var scenarioCards = BuildScenarioCards(active, upper, lower, isModelOpened, isApdlConfigured, isApdlRunning);

        var figureCount = LocalKnowledgeSearchService.CreateForApplication().DiscoverAllEmbeddedFigures().Count;
        var galleryBtnText = figureCount > 0 ? $"规范图库 ({figureCount})" : "规范图库";

        return new WelcomeDashboardSnapshot(
            ProjectName: projectName,
            VersionTag: versionTag,
            ModelCodeBadge: v.VehicleName,
            VehicleSubtitle: $"行业标准分类（{jtt.StandardCode}）：{v.StandardModelCode}（{v.StandardModelDescription}） · 总跨 {v.TotalSpanMm / 1000m:0.00} m · 两侧 {v.CantileverLengthMm / 1000m:0.00} m 大悬臂 · {v.MaterialGrade}",
            FollowUpNote: followUpNote,
            HasFollowUpNote: hasFollowUpNote,
            KbStatusText: kbBadgeText,
            IsKbReady: isKbReady,
            SolidWorksStatusText: swStatusText,
            IsSolidWorksOpened: isModelOpened,
            ApdlStatusText: apdlStatusText,
            IsApdlReady: isApdlConfigured,
            UpperChord: upperBrief,
            LowerChord: lowerBrief,
            RedlineItems: redlines,
            ScenarioCards: scenarioCards,
            IsApdlRunning: isApdlRunning,
            KbStatusTooltip: kbSummary,
            SolidWorksStatusTooltip: swStatusTooltip,
            ApdlStatusTooltip: apdlStatusTooltip,
            KnowledgeFigureGalleryButtonText: galleryBtnText);
    }

    private static bool IsAnsysApdlProcessRunning()
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (Services.Simulation.AnsysInstallationDetector.IsAnsysProcessName(proc.ProcessName))
                    {
                        return true;
                    }
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static WelcomeMemberBrief BuildMemberBrief(
        string roleTitle,
        MemberSectionSpecRecord spec,
        DesignBoundaryEvaluationResult eval,
        bool isUpper,
        DesignBoundaryProfile profile)
    {
        var g3811 = profile.GbT3811;
        var g50017 = profile.Gb50017;
        var jtt = profile.JtT1430;

        var badge = !eval.Mechanics.IsGeometryValid
            ? "几何异常"
            : eval.HasBoundaryWarning
                ? "壁厚 / 宽厚比有风险"
                : "壁厚 / 宽厚比合格";

        var badgeTooltip = !eval.Mechanics.IsGeometryValid
            ? "当前截面几何参数无效，请核对壁厚与外廓尺寸。"
            : $"壁厚核对（{g3811.StandardCode} 表34 中等腐蚀 ≥ {g3811.ClosedSectionMinWallModerateMm:0.#} mm）：{eval.CorrosionWallSummary}\n"
              + $"宽厚比与节点核对（{g50017.StandardCode} 表3.5.1 / 7.3.1 / 13.1.2）：{eval.WidthThicknessSummary}";

        var line1 = isUpper
            ? $"横向联动红管 {eval.Mechanics.LinkedUpperRedTubeLengthMm:0.#} mm · 水平通道净宽 {eval.Mechanics.EffectiveHorizontalWalkwayWidthMm:0.#} mm（{jtt.StandardCode} 表2）"
            : $"大悬臂受压控制 · 容许长细比 λ ≤ {g3811.ChordCompressionMaxSlenderness}（{g3811.StandardCode} 表28）";

        var line2 = isUpper
            ? $"净宽厚比 b₀/t = {eval.NetFlatWidthThicknessRatio:0.#}（{g50017.StandardCode} {eval.SectionWidthThicknessClass}）· 回转半径 r_min = {eval.MinGyrationRadiusMm:0.0} mm"
            : $"净宽厚比 b₀/t = {eval.NetFlatWidthThicknessRatio:0.#}（{g50017.StandardCode} {eval.SectionWidthThicknessClass}）· 外缘 b/t = {eval.OuterWidthThicknessRatio:0.#}（{g50017.StandardCode} 13.1.2）";

        return new WelcomeMemberBrief(
            RoleTitle: roleTitle,
            SpecText: $"{spec.SpecText} mm",
            StatusBadgeText: badge,
            HasWarning: eval.HasBoundaryWarning,
            Line1: line1,
            Line2: line2,
            StatusBadgeTooltip: badgeTooltip);
    }

    private static IReadOnlyList<WelcomeScenarioCardItem> BuildScenarioCards(
        DesignProjectRecord? active,
        MemberSectionSpecRecord upper,
        MemberSectionSpecRecord lower,
        bool isModelOpened,
        bool isApdlConfigured,
        bool isApdlRunning)
    {
        var isIterated = active is not null && active.CurrentVersionNumber > 0;

        // 左上：设计方案评估（模型 + 规范协同，不替用户硬塞具体目标尺寸）
        var consultTitle = isIterated ? "延续当前版本评估优化方向" : "本车轻量化思路与风险评估";
        var consultDesc = isIterated
            ? $"结合当前 {active!.VersionTag} 实时截面（上 {upper.SpecText} / 下 {lower.SpecText}），评估下一步优先调哪组杆件更稳妥"
            : "结合本车 7.35 m 大悬臂受力与已挂载规范，分析上/下弦杆截面与壁厚的优化方向及边界风险";
        var consultPrompt = isIterated
            ? $"结合我当前模型 {active!.VersionTag}（上弦杆 {upper.SpecText} mm、下弦杆 {lower.SpecText} mm），下一步轻量化减重优先怎么优化更稳妥？有哪些结构边界与工程风险建议？"
            : $"结合我当前模型（上弦杆 {upper.SpecText} mm、下弦杆 {lower.SpecText} mm）与本车 7.35 m 大悬臂特征，如果想开展轻量化减重设计，优先怎么优化更稳妥？有哪些结构边界与工程风险建议？";

        // 右上：SolidWorks 建模（未开模先开模；已开模再查可编辑构件，彻底消除前置冲突）
        var cadTitle = isModelOpened ? "查看可编辑构件与当前参数" : "打开当前 SolidWorks 工作模型";
        var cadDesc = isModelOpened
            ? "三维工作副本已就绪，查看当前支持参数化修改的上/下弦杆与联动规律，按需再发起改模"
            : "自动准备隔离工作副本并在 SolidWorks 中打开总装配体，便于边对话边查看三维联动变化";
        var cadPrompt = isModelOpened ? "查询可编辑参数" : "打开当前模型";
        var cadActionCue = isModelOpened ? "点击查看可编辑构件 →" : "点击执行：打开当前模型 →";

        // 右下：ANSYS APDL 仿真（独立承载仿真入口，四卡严格对应 设计/CAD/规范/仿真 四大域）
        var simCanLaunch = isApdlConfigured && !isApdlRunning;
        var simTitle = simCanLaunch ? "启动本地 ANSYS APDL 仿真" : "梳理本车仿真工况与验收红线";
        var simDesc = simCanLaunch
            ? "拉起本机已配置的 ANSYS APDL 启动器界面，准备母型与候选方案的强度、稳定及悬臂挠度对比"
            : "汇总额定工作、13.8 m/s 抗风、1.25 倍动载与 1.5 倍静载边界条件及 APDL 校核要点";
        var simPrompt = simCanLaunch
            ? "打开 APDL"
            : "结合我当前模型与已挂载规范，帮我梳理 ANSYS APDL 有限元校核需要设置的工况、载荷边界和验收判据";
        var simActionCue = simCanLaunch ? "点击执行：打开 APDL →" : "点击整理仿真工况 →";

        return
        [
            new WelcomeScenarioCardItem(
                Tag: "设计方案评估",
                TagBackgroundHex: "#FFE0F2FE",
                TagForegroundHex: "#FF0369A1",
                Title: consultTitle,
                Description: consultDesc,
                Prompt: consultPrompt,
                ActionCue: "点击发起方案评估 →"),
            new WelcomeScenarioCardItem(
                Tag: "SolidWorks 建模",
                TagBackgroundHex: "#FFECFDF5",
                TagForegroundHex: "#FF047857",
                Title: cadTitle,
                Description: cadDesc,
                Prompt: cadPrompt,
                ActionCue: cadActionCue),
            new WelcomeScenarioCardItem(
                Tag: "多规范公式溯源",
                TagBackgroundHex: "#FFEEF2FF",
                TagForegroundHex: "#FF4338CA",
                Title: "跨规范查证公式与核心限值",
                Description: "检索已挂载的 JT/T 1430、GB/T 3811、GB 50017、GB/T 6728、GB/T 1591 与 GB 50661 原文、公式与高清矢量附图",
                Prompt: "对比 JT/T 1430-2022 与 GB/T 3811-2008 对桥梁检查车悬臂静挠度、桁架弦杆长细比及 Q355B 许用应力的规定",
                ActionCue: "点击检索多规范原文 →"),
            new WelcomeScenarioCardItem(
                Tag: "ANSYS APDL 仿真",
                TagBackgroundHex: "#FFFFFBEB",
                TagForegroundHex: "#FFB45309",
                Title: simTitle,
                Description: simDesc,
                Prompt: simPrompt,
                ActionCue: simActionCue)
        ];
    }

    private static string FormatCompactKbBadge(string catalogSummary, bool isReady)
    {
        if (!isReady)
        {
            return "规范知识库待检测";
        }

        var countMatches = Regex.Matches(catalogSummary, @"(\d+)\s*项");
        var totalRules = 0;
        foreach (Match m in countMatches)
        {
            if (int.TryParse(m.Groups[1].Value, out var n))
            {
                totalRules += n;
            }
        }

        var packCount = Math.Max(1, countMatches.Count);
        return totalRules > 0
            ? $"{packCount} 本规范已挂载（{totalRules} 项）"
            : "多标准规范知识库已挂载";
    }
}
