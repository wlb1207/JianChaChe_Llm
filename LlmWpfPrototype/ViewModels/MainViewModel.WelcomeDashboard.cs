using System.Text.RegularExpressions;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services.Design;
using LlmWpfPrototype.Services.Rag;

namespace LlmWpfPrototype.ViewModels;

public sealed record WelcomeMemberBrief(
    string RoleTitle,
    string SpecText,
    string StatusBadgeText,
    bool HasWarning,
    string Line1,
    string Line2);

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
    IReadOnlyList<WelcomeScenarioCardItem> ScenarioCards);

public sealed partial class MainViewModel
{
    private WelcomeDashboardSnapshot? _welcomeDashboard;

    public bool ShowWelcomeDashboard =>
        HasActiveProject &&
        !HasPendingCadConfirmation &&
        ChatMessages.Count <= 1 &&
        ChatMessages.All(m => !m.IsUser);

    public bool ShowChatMessageStream =>
        HasActiveProject && !ShowWelcomeDashboard;

    public WelcomeDashboardSnapshot WelcomeDashboard =>
        _welcomeDashboard ??= BuildWelcomeDashboardSnapshot();

    private void RefreshWelcomeDashboard()
    {
        _welcomeDashboard = BuildWelcomeDashboardSnapshot();
        OnPropertyChanged(nameof(WelcomeDashboard));
        OnPropertyChanged(nameof(ShowWelcomeDashboard));
        OnPropertyChanged(nameof(ShowChatMessageStream));
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

        OnPropertyChanged(nameof(ShowWelcomeDashboard));
        OnPropertyChanged(nameof(ShowChatMessageStream));
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
            g3811);
        var lowerBrief = BuildMemberBrief(
            "下弦杆 · 斜桁架受压主弦",
            lower,
            lowerEval,
            isUpper: false,
            g3811);

        var isModelOpened = _solidWorksService.TryGetActiveDocumentInfoWithoutStartingSolidWorks(out _);
        var isApdlReady = IsConfiguredAnsysLauncherAvailable();

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
            $"悬臂挠度 ≤ {cantileverLimit:0.#} mm（{jtt.StandardCode} L'/{jtt.SteelCantileverDeflectionDivisor}）",
            $"{v.MaterialGrade} 许用应力 ≈ {allowableComboA:0.#} MPa（{g3811.StandardCode} 组合A）",
            $"闭口最小壁厚 ≥ {g3811.ClosedSectionMinWallModerateMm:0.#} mm（表34 中等腐蚀）",
            $"局部稳定 b₀/t ≤ {axialRatioLimit:0.#}（{g50017.StandardCode} 40εk）"
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

        var scenarioCards = BuildScenarioCards(active, upper, lower, isModelOpened, isApdlReady);

        return new WelcomeDashboardSnapshot(
            ProjectName: projectName,
            VersionTag: versionTag,
            ModelCodeBadge: $"{v.StandardModelCode} · 实体已绑定",
            VehicleSubtitle: $"{v.VehicleName} · 总跨 {v.TotalSpanMm / 1000m:0.00} m · 两侧 {v.CantileverLengthMm / 1000m:0.00} m 大悬臂 · {v.MaterialGrade}",
            FollowUpNote: followUpNote,
            HasFollowUpNote: hasFollowUpNote,
            KbStatusText: kbBadgeText,
            IsKbReady: isKbReady,
            SolidWorksStatusText: isModelOpened ? "SolidWorks 模型已打开" : "SolidWorks 工作区就绪",
            IsSolidWorksOpened: isModelOpened,
            ApdlStatusText: isApdlReady ? "ANSYS APDL 就绪" : "APDL 启动器待配置",
            IsApdlReady: isApdlReady,
            UpperChord: upperBrief,
            LowerChord: lowerBrief,
            RedlineItems: redlines,
            ScenarioCards: scenarioCards);
    }

    private static WelcomeMemberBrief BuildMemberBrief(
        string roleTitle,
        MemberSectionSpecRecord spec,
        DesignBoundaryEvaluationResult eval,
        bool isUpper,
        GbT3811BoundaryProfile g3811)
    {
        var badge = !eval.Mechanics.IsGeometryValid
            ? "几何异常"
            : eval.HasBoundaryWarning
                ? "含规范风险"
                : "表34 / 宽厚比正常";

        var line1 = isUpper
            ? $"横向联动红管 {eval.Mechanics.LinkedUpperRedTubeLengthMm:0.#} mm · 水平通道净宽 {eval.Mechanics.EffectiveHorizontalWalkwayWidthMm:0.#} mm"
            : $"大悬臂受压控制 · 容许长细比 λ ≤ {g3811.ChordCompressionMaxSlenderness}（表28）";

        var line2 = isUpper
            ? $"净宽厚比 b₀/t = {eval.NetFlatWidthThicknessRatio:0.#}（{eval.SectionWidthThicknessClass}）· 回转半径 r_min = {eval.MinGyrationRadiusMm:0.0} mm"
            : $"净宽厚比 b₀/t = {eval.NetFlatWidthThicknessRatio:0.#}（{eval.SectionWidthThicknessClass}）· 外缘 b/t = {eval.OuterWidthThicknessRatio:0.#}（节点13.1.2）";

        return new WelcomeMemberBrief(
            RoleTitle: roleTitle,
            SpecText: $"{spec.SpecText} mm",
            StatusBadgeText: badge,
            HasWarning: eval.HasBoundaryWarning,
            Line1: line1,
            Line2: line2);
    }

    private static IReadOnlyList<WelcomeScenarioCardItem> BuildScenarioCards(
        DesignProjectRecord? active,
        MemberSectionSpecRecord upper,
        MemberSectionSpecRecord lower,
        bool isModelOpened,
        bool isApdlReady)
    {
        var isIterated = active is not null && active.CurrentVersionNumber > 0;
        var consultTitle = isIterated ? "延续当前版本评估减重方向" : "本车减重方案与规范风险预检";
        var consultDesc = isIterated
            ? $"结合当前 {active!.VersionTag} 实体截面（上 {upper.SpecText} / 下 {lower.SpecText}），评估下一步优先调哪组杆件更稳妥"
            : "结合本车 7.35 m 悬臂与三本规范，对比下弦杆 3 mm 壁厚与 80×80×4 的局部稳定和防腐风险";
        var consultPrompt = isIterated
            ? $"结合我当前模型 {active!.VersionTag}（上弦杆 {upper.SpecText}、下弦杆 {lower.SpecText}），下一步轻量化减重优先怎么优化更稳妥？有哪些风险建议？"
            : "如果把下弦杆壁厚减到 3 mm，或者改成 80×80×4，结合当前模型怎么优化取舍，有哪些风险建议？";

        var upperAlready80x80x4 = upper.WidthMm == 80m && upper.HeightMm == 80m && upper.ThicknessMm == 4m;
        var cadPrompt = upperAlready80x80x4 ? "把下弦杆截面改成 70×70×4" : "把上弦杆截面改成 80×80×4";
        var cadDesc = upperAlready80x80x4
            ? "尝试仅将下弦杆减薄至 4 mm（外廓零改动），改模前自动核对表 34 与受压稳定红线"
            : "自动计算红管补偿与宽厚比边界，弹出风险预检并经您确认后再写入 SolidWorks";

        var toolTitle = isModelOpened && isApdlReady
            ? "一键启动本地 ANSYS APDL"
            : "打开当前 SolidWorks 工作模型";
        var toolDesc = isModelOpened && isApdlReady
            ? "三维模型已就绪，直接拉起本机 ANSYS APDL 界面开展悬臂挠度与静/动载校核"
            : "立即在 SolidWorks 中打开本项目的隔离工作副本，便于边对话边查看三维联动变化";
        var toolPrompt = isModelOpened && isApdlReady ? "打开 APDL" : "打开当前模型";

        return
        [
            new WelcomeScenarioCardItem(
                Tag: "模型 + 规范预检",
                TagBackgroundHex: "#FFE0F2FE",
                TagForegroundHex: "#FF0369A1",
                Title: consultTitle,
                Description: consultDesc,
                Prompt: consultPrompt,
                ActionCue: "点击发起方案评估 →"),
            new WelcomeScenarioCardItem(
                Tag: "三维联动改模",
                TagBackgroundHex: "#FFECFDF5",
                TagForegroundHex: "#FF047857",
                Title: "带规范预检的参数化改模",
                Description: cadDesc,
                Prompt: cadPrompt,
                ActionCue: $"点击预览：{cadPrompt} →"),
            new WelcomeScenarioCardItem(
                Tag: "三规范公式溯源",
                TagBackgroundHex: "#FFEEF2FF",
                TagForegroundHex: "#FF4338CA",
                Title: "跨规范查证公式与核心限值",
                Description: "检索 JT/T 1430、GB/T 3811 与 GB 50017 原文，完整呈现公式、表格与条款引用",
                Prompt: "对比 JT/T 1430-2022 与 GB/T 3811-2008 对桥梁检查车悬臂静挠度、桁架弦杆长细比及 Q355B 许用应力的规定",
                ActionCue: "点击检索三规范原文 →"),
            new WelcomeScenarioCardItem(
                Tag: "CAD / 仿真协同",
                TagBackgroundHex: "#FFFFFBEB",
                TagForegroundHex: "#FFB45309",
                Title: toolTitle,
                Description: toolDesc,
                Prompt: toolPrompt,
                ActionCue: $"点击执行：{toolPrompt} →")
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
