using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Chat;
using LlmWpfPrototype.Services.Design;
using LlmWpfPrototype.Services.Project;

namespace LlmWpfPrototype.ViewModels;

public sealed class DesignConversationItemViewModel : ObservableObject
{
    private bool _isSelected;
    private string _title = string.Empty;
    private DateTime _updatedAt;

    public required string Id { get; init; }

    public required string ProjectId { get; init; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (SetProperty(ref _updatedAt, value))
            {
                OnPropertyChanged(nameof(UpdatedTimeShort));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string UpdatedTimeShort =>
        UpdatedAt.Date == DateTime.Today ? UpdatedAt.ToString("HH:mm") : UpdatedAt.ToString("MM-dd");
}

public sealed class DesignProjectItemViewModel : ObservableObject
{
    private bool _isActive;
    private bool _isExpanded = true;

    public required DesignProjectRecord Record { get; init; }

    public string Id => Record.Id;

    public string Name => (Record.IsPinned ? "📌 " : "") + Record.Name;

    public string RawName => Record.Name;

    public string VersionBadgeText => Record.VersionBadgeText;

    public string WorkspaceBadgeText => Record.IsWorkspaceMaterialized ? "独立副本" : "按需生成";

    public string ArchiveActionText => Record.IsArchived ? "取消归档" : "归档项目";

    public string PinActionText => Record.IsPinned ? "取消置顶" : "置顶项目";

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandChevronText));
            }
        }
    }

    public string ExpandChevronText => IsExpanded ? "▾" : "▸";

    public ObservableCollection<DesignConversationItemViewModel> Conversations { get; } = [];

    public void RefreshMetadata()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(RawName));
        OnPropertyChanged(nameof(VersionBadgeText));
        OnPropertyChanged(nameof(WorkspaceBadgeText));
        OnPropertyChanged(nameof(ArchiveActionText));
        OnPropertyChanged(nameof(PinActionText));
    }
}

public sealed partial class MainViewModel
{
    private DesignProjectStoreService _projectStore = null!;
    private DesignConversationRecord? _activeConversation;
    private bool _isProjectSidebarVisible = true;
    private bool _isProjectFilterExpanded;
    private bool _isVersionPanelExpanded;
    private bool _isEditingProjectMeta;
    private string _projectSearchKeyword = string.Empty;
    private string _projectFilterMode = "Active"; // All | Active | Archived
    private string _editingProjectName = string.Empty;
    private string _editingProjectGoal = string.Empty;
    private bool _suppressDraftPersistence;
    private readonly HashSet<string> _collapsedProjectIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _isProbingLlmConnection;
    private string _llmStatusTooltip = "点击检测或重新连接 ChatGPT 服务";
    private int _llmStatusGeneration;

    public ObservableCollection<DesignProjectItemViewModel> FilteredProjects { get; } = [];

    public ObservableCollection<DesignVersionCheckpoint> ActiveVersionHistory { get; } = [];

    public IReadOnlyList<string> QuickPrompts { get; } =
    [
        "现在的截面尺寸是多少？",
        "如何合理调整桁架截面？",
        "打开当前模型",
        "主梁的安全系数该怎么选？"
    ];

    public RelayCommand ToggleSidebarCommand { get; private set; } = null!;

    public RelayCommand<DesignProjectItemViewModel> ToggleProjectExpandedCommand { get; private set; } = null!;

    public RelayCommand CancelPendingBusinessCommand { get; private set; } = null!;

    public AsyncRelayCommand<string> SendQuickPromptCommand { get; private set; } = null!;

    public AsyncRelayCommand ReconnectLlmCommand { get; private set; } = null!;

    public RelayCommand ToggleProjectFilterCommand { get; private set; } = null!;

    public RelayCommand<string> SetProjectFilterModeCommand { get; private set; } = null!;

    public AsyncRelayCommand CreateProjectCommand { get; private set; } = null!;

    public AsyncRelayCommand<DesignProjectItemViewModel> ForkProjectCommand { get; private set; } = null!;

    public AsyncRelayCommand CreateNewConversationCommand { get; private set; } = null!;

    public AsyncRelayCommand<DesignProjectItemViewModel> CreateConversationInProjectCommand { get; private set; } = null!;

    public AsyncRelayCommand<DesignProjectItemViewModel> SelectProjectCommand { get; private set; } = null!;

    public AsyncRelayCommand<DesignConversationItemViewModel> SelectConversationCommand { get; private set; } = null!;

    public RelayCommand<DesignProjectItemViewModel> BeginEditProjectMetaCommand { get; private set; } = null!;

    public RelayCommand SaveProjectMetaCommand { get; private set; } = null!;

    public RelayCommand CancelEditProjectMetaCommand { get; private set; } = null!;

    public RelayCommand<DesignProjectItemViewModel> TogglePinProjectCommand { get; private set; } = null!;

    public RelayCommand<DesignProjectItemViewModel> ToggleArchiveProjectCommand { get; private set; } = null!;

    public AsyncRelayCommand<DesignProjectItemViewModel> DeleteProjectCommand { get; private set; } = null!;

    public AsyncRelayCommand<DesignConversationItemViewModel> DeleteConversationCommand { get; private set; } = null!;

    public RelayCommand<DesignProjectItemViewModel> ExportProjectReportCommand { get; private set; } = null!;
    public RelayCommand<DesignProjectItemViewModel> ExportProjectWordReportCommand { get; private set; } = null!;

    public AsyncRelayCommand OpenCurrentProjectModelCommand { get; private set; } = null!;

    public AsyncRelayCommand<DesignProjectItemViewModel> OpenProjectModelCommand { get; private set; } = null!;

    public AsyncRelayCommand SyncLiveDimensionsCommand { get; private set; } = null!;

    public RelayCommand ToggleVersionPanelCommand { get; private set; } = null!;

    public AsyncRelayCommand<DesignVersionCheckpoint> RollbackToVersionCommand { get; private set; } = null!;

    public DesignProjectRecord? ActiveProject => _projectStore?.ActiveProject;

    public bool HasActiveProject => ActiveProject is not null;

    public string ActiveProjectDisplayName => ActiveProject?.Name ?? string.Empty;

    public bool HasZeroProjects => (_projectStore?.Projects.Count ?? 0) == 0;

    public bool ShowEmptyProjectsState => FilteredProjects.Count == 0;

    public string EmptyProjectsHintText => HasZeroProjects
        ? "暂无设计项目\n点击右上角或右侧按钮新建"
        : "没有匹配的设计项目";

    public bool IsProjectSidebarVisible
    {
        get => _isProjectSidebarVisible;
        set
        {
            if (SetProperty(ref _isProjectSidebarVisible, value))
            {
                OnPropertyChanged(nameof(ToggleSidebarTooltip));
            }
        }
    }

    public string ToggleSidebarTooltip => IsProjectSidebarVisible ? "收起项目管理侧边栏" : "展开项目管理侧边栏";

    public bool IsProjectFilterExpanded
    {
        get => _isProjectFilterExpanded;
        set => SetProperty(ref _isProjectFilterExpanded, value);
    }

    public bool IsVersionPanelExpanded
    {
        get => _isVersionPanelExpanded;
        set
        {
            if (SetProperty(ref _isVersionPanelExpanded, value))
            {
                OnPropertyChanged(nameof(VersionPanelButtonText));
            }
        }
    }

    public string VersionPanelButtonText => IsVersionPanelExpanded ? "收起版本与目标 ▴" : "版本与设计目标 ▾";

    public bool IsEditingProjectMeta
    {
        get => _isEditingProjectMeta;
        set => SetProperty(ref _isEditingProjectMeta, value);
    }

    public string EditingProjectName
    {
        get => _editingProjectName;
        set => SetProperty(ref _editingProjectName, value);
    }

    public string EditingProjectGoal
    {
        get => _editingProjectGoal;
        set => SetProperty(ref _editingProjectGoal, value);
    }

    public string ProjectSearchKeyword
    {
        get => _projectSearchKeyword;
        set
        {
            if (SetProperty(ref _projectSearchKeyword, value))
            {
                RebuildProjectSidebarItems();
            }
        }
    }

    public bool IsFilterAll => string.Equals(_projectFilterMode, "All", StringComparison.OrdinalIgnoreCase);

    public bool IsFilterActive => string.Equals(_projectFilterMode, "Active", StringComparison.OrdinalIgnoreCase);

    public bool IsFilterArchived => string.Equals(_projectFilterMode, "Archived", StringComparison.OrdinalIgnoreCase);

    public string CurrentProjectHeaderBadge => ActiveProject is { } active
        ? $"{active.Name}  ·  {_activeConversation?.Title ?? "新对话"}  ({active.VersionTag})"
        : "未选择项目";

    public string CurrentProjectGoalText => ActiveProject?.DesignGoalSummary ??
        "以 70×70×5 mm 为母型基准，优先开展轻量化减重设计。";

    public string CurrentVersionTag => ActiveProject?.VersionTag ?? "V0";

    public string CurrentUpperChordHud => $"上弦杆：{(ActiveProject?.UpperChord ?? MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆")).HudDisplayText}";

    public string CurrentLowerChordHud => $"下弦杆：{(ActiveProject?.LowerChord ?? MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆")).HudDisplayText}";

    public string CurrentLowerTrussHud => $"下桁架：{(ActiveProject?.LowerTruss ?? MemberSectionSpecRecord.CreateBaseline("lower_truss", "下桁架")).HudDisplayText}";

    public bool HasPendingCadConfirmation => _pendingBusinessPlan is not null && !IsAssistantBusy;

    public string PendingCadConfirmationSummary => _pendingBusinessPlan is null
        ? string.Empty
        : BuildPendingBusinessPlanSummary();

    public bool ShowWelcomeQuickPrompts =>
        HasActiveProject && ChatMessages.Count <= 1 && !IsAssistantBusy && !HasPendingCadConfirmation;

    public bool IsLlmStatusOnline => string.Equals(_llmRuntimeStatus, "在线", StringComparison.Ordinal);

    public bool IsLlmStatusConnecting => string.Equals(_llmRuntimeStatus, "连接中...", StringComparison.Ordinal);

    public bool IsLlmStatusError => string.Equals(_llmRuntimeStatus, "异常", StringComparison.Ordinal);

    public bool IsLlmStatusMock => string.Equals(_llmRuntimeStatus, "离线模拟", StringComparison.Ordinal);

    public string LlmStatusTooltip => _llmStatusTooltip;

    private void SetLlmRuntimeStatus(string status, string? tooltip = null)
    {
        _llmStatusGeneration++;
        _llmRuntimeStatus = status;
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            _llmStatusTooltip = tooltip;
        }

        OnPropertyChanged(nameof(LlmConfigSummary));
        OnPropertyChanged(nameof(GptStatusSummary));
        OnPropertyChanged(nameof(LlmStatusTooltip));
        OnPropertyChanged(nameof(IsLlmStatusOnline));
        OnPropertyChanged(nameof(IsLlmStatusConnecting));
        OnPropertyChanged(nameof(IsLlmStatusError));
        OnPropertyChanged(nameof(IsLlmStatusMock));
    }

    public async Task ProbeLlmConnectionAsync(bool isStartupProbe = false)
    {
        if (_isProbingLlmConnection)
        {
            return;
        }

        if (string.Equals(_options.Mode, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            SetLlmRuntimeStatus("离线模拟", "当前为本地模拟模式（不访问云端模型）");
            return;
        }

        _isProbingLlmConnection = true;
        ReconnectLlmCommand?.RaiseCanExecuteChanged();
        SetLlmRuntimeStatus("连接中...", isStartupProbe ? "启动自检：正在连接 ChatGPT 服务..." : "正在重新连接 ChatGPT 服务...");
        var probeStartGeneration = _llmStatusGeneration;

        try
        {
            var result = _llmService is LlmService concreteLlm
                ? await concreteLlm.ProbeConnectivityAsync()
                : await _llmService.TestConnectionAsync("pong");

            // If a real user turn already succeeded while the probe was in flight, do not overwrite it with an older result.
            if (_llmStatusGeneration != probeStartGeneration && IsLlmStatusOnline)
            {
                return;
            }

            if (result.Succeeded)
            {
                var latency = result.ElapsedMilliseconds.HasValue ? $"延迟 {result.ElapsedMilliseconds.Value} ms" : "已就绪";
                SetLlmRuntimeStatus("在线", $"ChatGPT 服务已连接（{latency}），点击可重新检测");
                if (isStartupProbe)
                {
                    AppendGlobalDiagnosticLogOnly($"[LLM Probe] 启动探测成功：{latency}");
                }
                else
                {
                    AppendLog($"[LLM Probe] 重连探测成功：{latency}");
                    SaveActiveConversationLogsSnapshot();
                }
            }
            else
            {
                var err = string.IsNullOrWhiteSpace(result.ExceptionMessage) ? "网络或代理异常" : result.ExceptionMessage;
                SetLlmRuntimeStatus("异常", $"连接异常：{err}（点击胶囊立即重试）");
                if (isStartupProbe)
                {
                    AppendGlobalDiagnosticLogOnly($"[LLM Probe] 启动探测失败：{err}");
                }
                else
                {
                    AppendLog($"[LLM Probe] 连接探测失败：{err}");
                    SaveActiveConversationLogsSnapshot();
                }
            }
        }
        catch (Exception ex)
        {
            if (_llmStatusGeneration == probeStartGeneration || !IsLlmStatusOnline)
            {
                SetLlmRuntimeStatus("异常", $"连接异常：{ex.Message}（点击胶囊立即重试）");
                if (isStartupProbe)
                {
                    AppendGlobalDiagnosticLogOnly($"[LLM Probe] 启动探测异常：{ex.Message}");
                }
                else
                {
                    AppendLog($"[LLM Probe] 连接探测异常：{ex.Message}");
                    SaveActiveConversationLogsSnapshot();
                }
            }
        }
        finally
        {
            _isProbingLlmConnection = false;
            ReconnectLlmCommand?.RaiseCanExecuteChanged();
        }
    }

    private void NotifyUiBusyAndConfirmationChanged()
    {
        OnPropertyChanged(nameof(IsAssistantBusy));
        OnPropertyChanged(nameof(IsCurrentConversationBusy));
        OnPropertyChanged(nameof(IsCadExclusiveOperationRunning));
        OnPropertyChanged(nameof(IsChatBusy));
        OnPropertyChanged(nameof(CanCancelActiveTask));
        OnPropertyChanged(nameof(CanSwitchProjectOrConversation));
        OnPropertyChanged(nameof(BusyBannerText));
        OnPropertyChanged(nameof(HasPendingCadConfirmation));
        OnPropertyChanged(nameof(PendingCadConfirmationSummary));
        OnPropertyChanged(nameof(CanConfirmBusiness));
        OnPropertyChanged(nameof(ShowWelcomeDashboard));
        OnPropertyChanged(nameof(ShowChatMessageStream));
        OnPropertyChanged(nameof(ShowWelcomeQuickPrompts));
        OnPropertyChanged(nameof(IsLlmStatusOnline));
        OnPropertyChanged(nameof(IsLlmStatusConnecting));
        OnPropertyChanged(nameof(IsLlmStatusError));
        OnPropertyChanged(nameof(IsLlmStatusMock));
        OnPropertyChanged(nameof(LlmStatusTooltip));
        _confirmBusinessCommand?.RaiseCanExecuteChanged();
        _cancelChatCommand?.RaiseCanExecuteChanged();
        CancelPendingBusinessCommand?.RaiseCanExecuteChanged();
        SendQuickPromptCommand?.RaiseCanExecuteChanged();
        PickImagesCommand?.RaiseCanExecuteChanged();
        ClearPendingImagesCommand?.RaiseCanExecuteChanged();
        SendImagePresetCommand?.RaiseCanExecuteChanged();
        ReconnectLlmCommand?.RaiseCanExecuteChanged();
        CreateProjectCommand?.RaiseCanExecuteChanged();
        CreateNewConversationCommand?.RaiseCanExecuteChanged();
        CreateConversationInProjectCommand?.RaiseCanExecuteChanged();
        SelectProjectCommand?.RaiseCanExecuteChanged();
        SelectConversationCommand?.RaiseCanExecuteChanged();
        DeleteProjectCommand?.RaiseCanExecuteChanged();
        DeleteConversationCommand?.RaiseCanExecuteChanged();
        ForkProjectCommand?.RaiseCanExecuteChanged();
        OpenCurrentProjectModelCommand?.RaiseCanExecuteChanged();
        OpenProjectModelCommand?.RaiseCanExecuteChanged();
        SyncLiveDimensionsCommand?.RaiseCanExecuteChanged();
        RollbackToVersionCommand?.RaiseCanExecuteChanged();
    }

    public string CurrentProjectWorkspaceSummary
    {
        get
        {
            if (ActiveProject is null)
            {
                return "⚪ 暂无活动项目";
            }

            var isModelOpen = _solidWorksService.TryGetActiveDocumentInfoWithoutStartingSolidWorks(out var activeDocPath);
            if (isModelOpen && IsActiveDocumentInCurrentProjectWorkspace(activeDocPath))
            {
                return "🟢 SolidWorks 已挂载本项目";
            }

            if (isModelOpen)
            {
                return "🟡 SW 已打开其他目录模型";
            }

            return ActiveProject.IsWorkspaceMaterialized
                ? "⚪ 模型副本就绪 · SW 未打开"
                : "⚪ 概念推演态 · 按需生成副本";
        }
    }

    public void FlushActiveSessionOnExit()
    {
        if (_projectStore is null || _activeConversation is null)
        {
            return;
        }

        _activeConversation.DraftInput = _userInput ?? string.Empty;
        PersistActiveConversationSnapshot();
    }

    private void InitializeProjectManagement(DesignProjectStoreService? injectedStore)
    {
        var wsOptions = new SolidWorksWorkspaceOptions
        {
            ModelRootPath = _workspaceManager.ModelRootPath,
            InitialModelFolder = _workspaceManager.InitialModelFolder,
            WorkingModelFolder = _workspaceManager.DefaultWorkingModelFolder,
            AssemblyFileName = _workspaceManager.AssemblyFileName
        };

        _projectStore = injectedStore ?? new DesignProjectStoreService(
            Path.Combine(Path.GetTempPath(), "SanliuSessionProjects", Guid.NewGuid().ToString("N")),
            wsOptions);

        ToggleSidebarCommand = new RelayCommand(() => IsProjectSidebarVisible = !IsProjectSidebarVisible);
        ToggleProjectExpandedCommand = new RelayCommand<DesignProjectItemViewModel>(item =>
        {
            if (item is null) return;
            item.IsExpanded = !item.IsExpanded;
            if (item.IsExpanded)
            {
                _collapsedProjectIds.Remove(item.Id);
            }
            else
            {
                _collapsedProjectIds.Add(item.Id);
            }
        });
        CancelPendingBusinessCommand = new RelayCommand(() =>
        {
            if (_pendingBusinessPlan is null) return;
            ClearPendingBusinessPlan();
            AppendLog("[Confirmation] DimensionPlanCancelledByUser; Dispatched=False");
            const string cancellationReply = "已取消这项待确认的尺寸修改，未执行 CAD 写入。";
            AddAssistantMessage(cancellationReply);
            AppendConversationHistory("取消修改", cancellationReply);
        }, () => _pendingBusinessPlan is not null && !IsAssistantBusy);
        SendQuickPromptCommand = new AsyncRelayCommand<string>(async prompt =>
        {
            if (string.IsNullOrWhiteSpace(prompt) || IsAssistantBusy) return;
            IsChatBriefingExpanded = false;
            UserInput = prompt;
            await PrimaryActionCommand.ExecuteAsync();
        }, _ => !IsAssistantBusy);
        ReconnectLlmCommand = new AsyncRelayCommand(() => ProbeLlmConnectionAsync(isStartupProbe: false), () => !_isProbingLlmConnection);
        ToggleProjectFilterCommand = new RelayCommand(() => IsProjectFilterExpanded = !IsProjectFilterExpanded);
        SetProjectFilterModeCommand = new RelayCommand<string>(mode =>
        {
            _projectFilterMode = string.IsNullOrWhiteSpace(mode) ? "Active" : mode;
            OnPropertyChanged(nameof(IsFilterAll));
            OnPropertyChanged(nameof(IsFilterActive));
            OnPropertyChanged(nameof(IsFilterArchived));
            RebuildProjectSidebarItems();
        });

        CreateProjectCommand = new AsyncRelayCommand(CreateAndSwitchProjectAsync, () => CanSwitchProjectOrConversation);
        ForkProjectCommand = new AsyncRelayCommand<DesignProjectItemViewModel>(ForkAndSwitchProjectAsync, _ => CanSwitchProjectOrConversation);
        CreateNewConversationCommand = new AsyncRelayCommand(() => CreateAndSwitchConversationAsync(ActiveProject?.Id), () => CanSwitchProjectOrConversation);
        CreateConversationInProjectCommand = new AsyncRelayCommand<DesignProjectItemViewModel>(
            item => CreateAndSwitchConversationAsync(item?.Id ?? ActiveProject?.Id),
            _ => CanSwitchProjectOrConversation);
        SelectProjectCommand = new AsyncRelayCommand<DesignProjectItemViewModel>(
            item => item is null ? Task.CompletedTask : SwitchToProjectAsync(item.Id),
            _ => CanSwitchProjectOrConversation);
        SelectConversationCommand = new AsyncRelayCommand<DesignConversationItemViewModel>(
            item => item is null ? Task.CompletedTask : SwitchToConversationAsync(item.ProjectId, item.Id),
            _ => CanSwitchProjectOrConversation);

        BeginEditProjectMetaCommand = new RelayCommand<DesignProjectItemViewModel>(item =>
        {
            var target = item?.Record ?? ActiveProject;
            if (target is null) return;
            if (!string.Equals(target.Id, ActiveProject?.Id, StringComparison.OrdinalIgnoreCase))
            {
                _ = SwitchToProjectAsync(target.Id);
            }
            EditingProjectName = target.Name;
            EditingProjectGoal = target.DesignGoalSummary;
            IsProjectSidebarVisible = true;
            IsEditingProjectMeta = true;
        });

        SaveProjectMetaCommand = new RelayCommand(() =>
        {
            if (ActiveProject is not { } active)
            {
                IsEditingProjectMeta = false;
                return;
            }
            if (!string.IsNullOrWhiteSpace(EditingProjectName))
            {
                _projectStore.RenameProject(active.Id, EditingProjectName);
            }
            active.DesignGoalSummary = string.IsNullOrWhiteSpace(EditingProjectGoal)
                ? "以 70×70×5 mm 为母型基准，在满足 Q355B 强度、7.35 m 大悬臂刚度及防干涉约束下优先开展轻量化减重设计。"
                : EditingProjectGoal.Trim();
            _projectStore.SaveProject(active);
            IsEditingProjectMeta = false;
            RefreshProjectHudProperties();
            RebuildProjectSidebarItems();
            StatusMessage = $"已保存项目“{active.Name}”名称与设计目标。";
        });

        CancelEditProjectMetaCommand = new RelayCommand(() => IsEditingProjectMeta = false);

        TogglePinProjectCommand = new RelayCommand<DesignProjectItemViewModel>(item =>
        {
            var target = item?.Record ?? ActiveProject;
            if (target is null) return;
            _projectStore.TogglePinProject(target.Id);
            RebuildProjectSidebarItems();
        });

        ToggleArchiveProjectCommand = new RelayCommand<DesignProjectItemViewModel>(item =>
        {
            var target = item?.Record ?? ActiveProject;
            if (target is null) return;
            _projectStore.ToggleArchiveProject(target.Id);
            RebuildProjectSidebarItems();
        });

        DeleteProjectCommand = new AsyncRelayCommand<DesignProjectItemViewModel>(async item =>
        {
            var target = item?.Record ?? ActiveProject;
            if (target is null) return;
            var deletingActive = string.Equals(target.Id, ActiveProject?.Id, StringComparison.OrdinalIgnoreCase);
            if (_projectStore.DeleteProject(target.Id))
            {
                _collapsedProjectIds.Remove(target.Id);
                if (deletingActive)
                {
                    if (_projectStore.ActiveProject is { } nextActive)
                    {
                        await ActivateProjectAndConversationCoreAsync(nextActive, null, persistCurrentFirst: false);
                    }
                    else
                    {
                        await ActivateEmptyWorkspaceAsync();
                    }
                }
                else
                {
                    RebuildProjectSidebarItems();
                }
                StatusMessage = $"已移除项目：{target.Name}";
            }
        }, _ => CanSwitchProjectOrConversation);

        DeleteConversationCommand = new AsyncRelayCommand<DesignConversationItemViewModel>(async item =>
        {
            if (item is null) return;
            var deletingCurrent = string.Equals(item.Id, _activeConversation?.Id, StringComparison.OrdinalIgnoreCase);
            _projectStore.DeleteConversation(item.ProjectId, item.Id);
            var proj = _projectStore.GetProjectById(item.ProjectId) ?? ActiveProject;
            if (proj is not null && deletingCurrent && string.Equals(proj.Id, ActiveProject?.Id, StringComparison.OrdinalIgnoreCase))
            {
                await ActivateProjectAndConversationCoreAsync(proj, proj.ActiveConversationId, persistCurrentFirst: false);
            }
            else
            {
                RebuildProjectSidebarItems();
            }
        }, _ => CanSwitchProjectOrConversation);

        ExportProjectReportCommand = new RelayCommand<DesignProjectItemViewModel>(item =>
        {
            var target = item?.Record ?? ActiveProject;
            if (target is null) return;
            var path = _projectStore.ExportProjectMarkdownReport(target.Id, AppPaths.ReportsDirectory);
            AppendLog($"[Project] 已导出项目设计报告：{path}");
            StatusMessage = $"已导出项目报告：{Path.GetFileName(path)}（位于 reports 目录）";
            AddAssistantMessage($"已导出项目《{target.Name}》完整设计演进与对话归档报告：`{Path.GetFileName(path)}`（保存在程序 `reports` 文件夹下，可直接用于论文实验与方案整理）。");
            PersistActiveConversationSnapshot();
        });

        ExportProjectWordReportCommand = new RelayCommand<DesignProjectItemViewModel>(item =>
        {
            var target = item?.Record ?? ActiveProject;
            if (target is null) return;
            var path = _projectStore.ExportProjectWordReport(target.Id, AppPaths.ReportsDirectory);
            AppendLog($"[Project] 已导出图文 Word 计算书：{path}");
            StatusMessage = $"已导出图文 Word 计算书：{Path.GetFileName(path)}（位于 reports 目录）";
            AddAssistantMessage($"已生成项目《{target.Name}》图文 Word 设计计算书：`{Path.GetFileName(path)}`（含双口径力学对比、6 规范边界核对清单、高清矢量结构附图与版本演进时间线，保存在 `reports` 文件夹）。");
            PersistActiveConversationSnapshot();
        });

        OpenCurrentProjectModelCommand = new AsyncRelayCommand(OpenCurrentProjectModelFromUiAsync, () => CanSwitchProjectOrConversation);
        OpenProjectModelCommand = new AsyncRelayCommand<DesignProjectItemViewModel>(async item =>
        {
            if (item is not null && !string.Equals(item.Id, ActiveProject?.Id, StringComparison.OrdinalIgnoreCase))
            {
                await SwitchToProjectAsync(item.Id);
            }
            await OpenCurrentProjectModelFromUiAsync();
        }, _ => CanSwitchProjectOrConversation);
        SyncLiveDimensionsCommand = new AsyncRelayCommand(SyncLiveDimensionsFromUiAsync, () => CanSwitchProjectOrConversation);
        ToggleVersionPanelCommand = new RelayCommand(() => IsVersionPanelExpanded = !IsVersionPanelExpanded);
        RollbackToVersionCommand = new AsyncRelayCommand<DesignVersionCheckpoint>(QueueVersionRollbackConfirmationAsync, _ => CanSwitchProjectOrConversation);

        // Activate initial or last-used project & conversation synchronously (or clean empty state if 0 projects)
        if (ActiveProject is { } initialProject)
        {
            ActivateProjectAndConversationCoreAsync(initialProject, initialProject.ActiveConversationId, persistCurrentFirst: false)
                .GetAwaiter().GetResult();
        }
        else
        {
            ActivateEmptyWorkspaceAsync().GetAwaiter().GetResult();
        }
    }

    private async Task ActivateEmptyWorkspaceAsync()
    {
        ClearPendingBusinessPlan();
        ClearPendingImageAttachments();
        ClearPendingModificationContext("empty_project_workspace");
        _pendingResetWorkspaceConfirmation = false;
        _chatRetrievalTopic = null;
        _activeConversation = null;
        IsEditingProjectMeta = false;
        IsVersionPanelExpanded = false;
        _lastModifiedParameterSummary = string.Empty;

        await _workspaceManager.SwitchProjectWorkspaceAsync(
            _workspaceManager.DefaultWorkingModelFolder,
            isDefaultProject: true,
            isMaterialized: false,
            message => AppendGlobalDiagnosticLogOnly(message));

        ChatMessages.Clear();
        _memoryStore.Clear();
        LoadConversationLogEntries([]);

        _suppressDraftPersistence = true;
        try
        {
            UserInput = string.Empty;
        }
        finally
        {
            _suppressDraftPersistence = false;
        }

        SyncVersionHistoryCollection();
        RefreshProjectHudProperties();
        RebuildProjectSidebarItems();
        NotifyUiBusyAndConfirmationChanged();
    }

    private async Task EnsureActiveProjectForIncomingMessageAsync()
    {
        if (_projectStore is null || ActiveProject is not null)
        {
            return;
        }

        var created = _projectStore.CreateProject();
        var conv = _projectStore.LoadOrCreateActiveConversation(created);
        _activeConversation = conv;
        created.ActiveConversationId = conv.Id;

        await _workspaceManager.SwitchProjectWorkspaceAsync(
            created.WorkspaceFolder,
            created.IsDefaultProject,
            created.IsWorkspaceMaterialized,
            message => AppendGlobalDiagnosticLogOnly(message));

        LoadConversationLogEntries(_projectStore.LoadConversationLogs(created.Id, conv.Id));

        SyncVersionHistoryCollection();
        RefreshProjectHudProperties();
        RebuildProjectSidebarItems();
        NotifyTaskIsolationStateChanged();
    }

    private async Task CreateAndSwitchProjectAsync()
    {
        PersistActiveConversationSnapshot();
        var created = _projectStore.CreateProject();
        await ActivateProjectAndConversationCoreAsync(created, created.ActiveConversationId, persistCurrentFirst: false);
        StatusMessage = $"已新建项目“{created.Name}”（概念推演态，按需自动生成独立模型副本）。";
    }

    private async Task ForkAndSwitchProjectAsync(DesignProjectItemViewModel? sourceItem)
    {
        PersistActiveConversationSnapshot();
        var sourceId = sourceItem?.Id ?? ActiveProject?.Id;
        var forked = _projectStore.ForkProject(sourceId);
        await ActivateProjectAndConversationCoreAsync(forked, forked.ActiveConversationId, persistCurrentFirst: false);
        StatusMessage = $"已基于原方案派生新项目“{forked.Name}”（继承 {forked.VersionTag} 截面状态）。";
    }

    private async Task CreateAndSwitchConversationAsync(string? projectId)
    {
        PersistActiveConversationSnapshot();
        if (string.IsNullOrWhiteSpace(projectId) && ActiveProject is null)
        {
            await CreateAndSwitchProjectAsync();
            return;
        }

        var targetProjectId = projectId ?? ActiveProject!.Id;
        if (!string.Equals(targetProjectId, ActiveProject?.Id, StringComparison.OrdinalIgnoreCase))
        {
            _projectStore.SetActiveProject(targetProjectId);
        }

        var active = ActiveProject ?? _projectStore.CreateProject();
        var conv = _projectStore.CreateConversation(active.Id);
        await ActivateProjectAndConversationCoreAsync(active, conv.Id, persistCurrentFirst: false);
        StatusMessage = $"已在项目“{active.Name}”下开启新跟进对话（共享 {active.VersionTag} 结构快照）。";
    }

    private async Task SwitchToProjectAsync(string projectId)
    {
        if (string.Equals(projectId, ActiveProject?.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var target = _projectStore.GetProjectById(projectId);
        if (target is null)
        {
            return;
        }

        await ActivateProjectAndConversationCoreAsync(target, target.ActiveConversationId, persistCurrentFirst: true);
        StatusMessage = $"已切换至项目“{target.Name}”（{target.VersionTag}）。";
    }

    private async Task SwitchToConversationAsync(string projectId, string conversationId)
    {
        if (string.Equals(projectId, ActiveProject?.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(conversationId, _activeConversation?.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var targetProj = _projectStore.GetProjectById(projectId) ?? ActiveProject;
        if (targetProj is null)
        {
            return;
        }

        await ActivateProjectAndConversationCoreAsync(targetProj, conversationId, persistCurrentFirst: true);
        StatusMessage = $"已加载历史对话“{_activeConversation?.Title ?? "新对话"}”。";
    }

    private async Task ActivateProjectAndConversationCoreAsync(
        DesignProjectRecord project,
        string? conversationId,
        bool persistCurrentFirst)
    {
        if (persistCurrentFirst && _activeConversation is not null)
        {
            PersistActiveConversationSnapshot();
        }

        ClearPendingBusinessPlan();
        ClearPendingImageAttachments();
        ClearPendingModificationContext("project_or_conversation_switch");
        _pendingResetWorkspaceConfirmation = false;
        _chatRetrievalTopic = null;

        _projectStore.SetActiveProject(project.Id);

        await _workspaceManager.SwitchProjectWorkspaceAsync(
            project.WorkspaceFolder,
            project.IsDefaultProject,
            project.IsWorkspaceMaterialized,
            message => AppendGlobalDiagnosticLogOnly(message));

        var targetConv = (!string.IsNullOrWhiteSpace(conversationId)
            ? _projectStore.TryLoadConversation(project.Id, conversationId)
            : null) ?? _projectStore.LoadOrCreateActiveConversation(project);

        _activeConversation = targetConv;
        project.ActiveConversationId = targetConv.Id;

        LoadConversationLogEntries(_projectStore.LoadConversationLogs(project.Id, targetConv.Id));

        _lastModifiedParameterSummary = project.CurrentVersionNumber > 0
            ? (project.VersionHistory.LastOrDefault()?.Summary ?? project.VersionBadgeText)
            : string.Empty;

        IsChatBriefingExpanded = false;
        ChatMessages.Clear();
        _memoryStore.RestoreFromPersisted(targetConv.Turns, targetConv.Actions);

        if (targetConv.Messages.Count > 0)
        {
            foreach (var msg in targetConv.Messages)
            {
                ChatMessages.Add(msg.ToRuntimeChatMessage());
            }
        }
        else
        {
            AddWelcomeAnchorMessage(BuildWelcomeMessage());
            PersistActiveConversationSnapshot();
        }

        _suppressDraftPersistence = true;
        try
        {
            UserInput = targetConv.DraftInput ?? string.Empty;
        }
        finally
        {
            _suppressDraftPersistence = false;
        }

        SyncVersionHistoryCollection();
        RefreshProjectHudProperties();
        RebuildProjectSidebarItems();
        NotifyTaskIsolationStateChanged();
    }

    private void PersistActiveConversationSnapshot(string? latestUserInputForAutoTitle = null)
    {
        if (_projectStore is null || _activeConversation is null || ActiveProject is not { } active)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(latestUserInputForAutoTitle))
        {
            active.Metrics.TotalUserTurns++;
            _projectStore.AutoUpdateConversationTitleIfDefault(active, _activeConversation, latestUserInputForAutoTitle);
        }

        _activeConversation.DraftInput = _userInput ?? string.Empty;
        _activeConversation.Messages = ChatMessages.Select(PersistedChatMessage.FromRuntimeChatMessage).ToList();
        _activeConversation.Turns = _memoryStore.ExportPersistedTurns();
        _activeConversation.Actions = _memoryStore.ExportPersistedActions();

        _projectStore.SaveConversationState(active, _activeConversation);
        SaveActiveConversationLogsSnapshot();
        OnPropertyChanged(nameof(CurrentProjectHeaderBadge));
        OnPropertyChanged(nameof(CurrentLogScopeSummary));
        UpdateSidebarConversationItemInPlace();
    }

    private void SaveActiveConversationLogsSnapshot()
    {
        if (_projectStore is null || _activeConversation is null || ActiveProject is not { } active)
        {
            return;
        }

        _projectStore.SaveConversationLogs(active.Id, _activeConversation.Id, LogEntries);
    }

    private void SaveDraftInputIfReady()
    {
        if (_suppressDraftPersistence || _projectStore is null || _activeConversation is null)
        {
            return;
        }

        _activeConversation.DraftInput = _userInput ?? string.Empty;
    }

    private async Task OpenCurrentProjectModelFromUiAsync()
    {
        if (IsCadExclusiveOperationRunning)
        {
            return;
        }

        await EnsureActiveProjectForIncomingMessageAsync();
        var parse = new LlmParseResult
        {
            Actions = ["open_working_model"],
            Commands = ["open_working_model"]
        };
        _lastSubmittedInput = "打开当前项目工作模型";
        AddUserMessage("打开当前项目工作模型");
        PersistActiveConversationSnapshot("打开当前项目工作模型");
        var task = BeginConversationTask("正在准备工作区并打开 SolidWorks 工作模型（三维操作期间暂不可切换项目）...", isCadExclusive: true);
        try
        {
            await HandleParsedResultAsync(parse, string.Empty, "打开当前项目工作模型", isConfirmedCadPlan: true, task: task);
        }
        catch (OperationCanceledException)
        {
            const string cancelMsg = "已取消打开 SolidWorks 模型操作。";
            AppendLogForTask(task, "[SolidWorks] 用户取消打开工作模型。");
            AddAssistantMessageForTask(task, cancelMsg, roleDisplay: "系统");
            StatusMessage = cancelMsg;
            PersistActiveConversationSnapshot();
        }
        finally
        {
            EndConversationTask(task);
            RefreshProjectHudProperties();
            RebuildProjectSidebarItems();
        }
    }

    private async Task SyncLiveDimensionsFromUiAsync()
    {
        var synced = await RefreshLiveCadDimensionsSilentlyAsync(forceLog: true);
        if (synced)
        {
            var msg = $"已从当前打开的 SolidWorks 工作模型同步实测杆件截面尺寸（外宽×外高×壁厚）：\n" +
                      $"- {CurrentUpperChordHud}\n- {CurrentLowerChordHud}";
            StatusMessage = "已同步 SolidWorks 实时杆件截面尺寸。";
            AddAssistantMessage(msg);
            AppendConversationHistory("同步实测尺寸", msg);
        }
        else
        {
            StatusMessage = "当前未检测到已挂载的本项目 SolidWorks 总装配，展示已保存的截面尺寸。";
            AddAssistantMessage($"当前 SolidWorks 尚未打开本项目的总装配体，当前保存的杆件截面尺寸（外宽×外高×壁厚）为：\n" +
                                $"- {CurrentUpperChordHud}\n- {CurrentLowerChordHud}\n" +
                                "如需实测核对，可直接发消息“打开当前模型”。");
            PersistActiveConversationSnapshot();
        }
    }

    private async Task<bool> RefreshLiveCadDimensionsSilentlyAsync(bool forceLog = false)
    {
        if (ActiveProject is not { } active ||
            _solidWorksService is not SolidWorksService liveCad ||
            !IsActiveDocumentInCurrentProjectWorkspace())
        {
            return false;
        }

        var enabledMembers = _editableTrussMemberCatalogService.GetEnabledMembers().ToArray();
        var measured = await liveCad.TryReadLiveMemberSectionsAsync(
            enabledMembers,
            forceLog ? message => AppendLog(message) : null);

        if (measured.Count == 0)
        {
            return false;
        }

        _projectStore.UpdateLiveMeasuredSections(active, measured);
        RefreshProjectHudProperties();
        RebuildProjectSidebarItems();
        return true;
    }

    private async Task QueueVersionRollbackConfirmationAsync(DesignVersionCheckpoint? targetCheckpoint)
    {
        if (targetCheckpoint is null || ActiveProject is not { } active)
        {
            return;
        }

        var diffSegments = new List<string>();
        var rollbackSteps = new List<(string CommandText, List<LlmParsedParameter> Parameters, string Label)>();

        if (!SpecsEqual(active.UpperChord, targetCheckpoint.UpperChord))
        {
            var u = targetCheckpoint.UpperChord;
            var label = $"上弦杆 -> {u.SpecText}";
            diffSegments.Add(label);
            var upperParams = new List<LlmParsedParameter>
            {
                new() { Name = "truss_top_chord_section_width", DisplayName = "上弦杆截面宽度", Value = JsonSerializerHelperToElement(u.WidthMm), Unit = "mm", Target = ["solidworks"] },
                new() { Name = "truss_top_chord_section_height", DisplayName = "上弦杆截面高度", Value = JsonSerializerHelperToElement(u.HeightMm), Unit = "mm", Target = ["solidworks"] },
                new() { Name = "truss_top_chord_wall_thickness", DisplayName = "上弦杆壁厚", Value = JsonSerializerHelperToElement(u.ThicknessMm), Unit = "mm", Target = ["solidworks"] }
            };
            rollbackSteps.Add(($"把桁架上弦杆截面改成 {u.WidthMm:0.##}x{u.HeightMm:0.##}x{u.ThicknessMm:0.##}", upperParams, label));
        }

        var lowerChordChanged = !SpecsEqual(active.LowerChord, targetCheckpoint.LowerChord);
        if (lowerChordChanged)
        {
            var lc = targetCheckpoint.LowerChord;
            var label = $"下弦杆 -> {lc.SpecText}";
            diffSegments.Add(label);
            var lowerParams = new List<LlmParsedParameter>
            {
                new() { Name = "truss_bottom_chord_section_width", DisplayName = "下弦杆截面宽度", Value = JsonSerializerHelperToElement(lc.WidthMm), Unit = "mm", Target = ["solidworks"] },
                new() { Name = "truss_bottom_chord_section_height", DisplayName = "下弦杆截面高度", Value = JsonSerializerHelperToElement(lc.HeightMm), Unit = "mm", Target = ["solidworks"] },
                new() { Name = "truss_bottom_chord_wall_thickness", DisplayName = "下弦杆壁厚", Value = JsonSerializerHelperToElement(lc.ThicknessMm), Unit = "mm", Target = ["solidworks"] }
            };
            rollbackSteps.Add(($"把桁架下弦杆截面改成 {lc.WidthMm:0.##}x{lc.HeightMm:0.##}x{lc.ThicknessMm:0.##}", lowerParams, label));
        }
        else if (!SpecsEqual(active.LowerTruss, targetCheckpoint.LowerTruss))
        {
            var lt = targetCheckpoint.LowerTruss;
            var label = $"下桁架 -> {lt.SpecText}";
            diffSegments.Add(label);
            var ltParams = new List<LlmParsedParameter>
            {
                new() { Name = "lower_truss_section_width", DisplayName = "下桁架截面宽度", Value = JsonSerializerHelperToElement(lt.WidthMm), Unit = "mm", Target = ["solidworks"] },
                new() { Name = "lower_truss_section_height", DisplayName = "下桁架截面高度", Value = JsonSerializerHelperToElement(lt.HeightMm), Unit = "mm", Target = ["solidworks"] },
                new() { Name = "lower_truss_wall_thickness", DisplayName = "下桁架壁厚", Value = JsonSerializerHelperToElement(lt.ThicknessMm), Unit = "mm", Target = ["solidworks"] }
            };
            rollbackSteps.Add(($"把下桁架改成 {lt.WidthMm:0.##}x{lt.HeightMm:0.##}x{lt.ThicknessMm:0.##}", ltParams, label));
        }

        if (rollbackSteps.Count == 0)
        {
            StatusMessage = $"当前结构参数已与 {targetCheckpoint.VersionTag} 一致，无需回退。";
            return;
        }

        var builtPlans = new List<QueuedRollbackStep>();
        foreach (var step in rollbackSteps)
        {
            if (_trussMemberCommandParser.TryParse(step.CommandText, out _) &&
                TryBuildDeterministicRollbackPlan(step.CommandText, step.Parameters, out var stepPlan))
            {
                builtPlans.Add(new QueuedRollbackStep(step.CommandText, stepPlan, step.Label));
            }
        }

        if (builtPlans.Count == 0)
        {
            StatusMessage = $"未能为 {targetCheckpoint.VersionTag} 构建可执行的回退计划。";
            return;
        }

        AddUserMessage($"回退至 {targetCheckpoint.VersionTag}（{string.Join("，", diffSegments)}）");
        _pendingFollowUpRollbackSteps.Clear();
        for (var i = 1; i < builtPlans.Count; i++)
        {
            _pendingFollowUpRollbackSteps.Add(builtPlans[i]);
        }

        var primaryStep = builtPlans[0];
        _lastSubmittedInput = primaryStep.CommandText;
        BindParameters(primaryStep.Plan);
        QueueBusinessPlanForConfirmation(primaryStep.Plan, $"回退至 {targetCheckpoint.VersionTag}");

        await Task.CompletedTask;
    }

    private bool TryBuildDeterministicRollbackPlan(
        string commandText,
        List<LlmParsedParameter> fallbackParameters,
        out LlmParseResult plan)
    {
        if (TryBuildDeterministicLowerTrussParseResult(commandText, out plan) ||
            TryBuildDeterministicLowerChordParseResult(commandText, out plan) ||
            TryBuildDeterministicUpperChordParseResult(commandText, out plan, out _))
        {
            return true;
        }

        plan = new LlmParseResult
        {
            Actions = ["update_solidworks_dimensions"],
            Commands = ["update_solidworks_dimensions"],
            Parameters = fallbackParameters,
            NeedConfirmation = true
        };
        return true;
    }

    private static System.Text.Json.JsonElement JsonSerializerHelperToElement(decimal value)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(value.ToString(CultureInfo.InvariantCulture));
        return doc.RootElement.Clone();
    }

    private static bool SpecsEqual(MemberSectionSpecRecord a, MemberSectionSpecRecord b) =>
        a.WidthMm == b.WidthMm && a.HeightMm == b.HeightMm && a.ThicknessMm == b.ThicknessMm;

    private bool IsActiveDocumentInCurrentProjectWorkspace(string? activeDocPath = null)
    {
        var path = activeDocPath;
        if (string.IsNullOrWhiteSpace(path) &&
            !_solidWorksService.TryGetActiveDocumentInfoWithoutStartingSolidWorks(out path))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(_workspaceManager.WorkingModelFolder))
        {
            return false;
        }

        var fullDoc = Path.GetFullPath(path);
        var fullWork = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_workspaceManager.WorkingModelFolder)) + Path.DirectorySeparatorChar;
        return fullDoc.StartsWith(fullWork, StringComparison.OrdinalIgnoreCase);
    }

    private void RecordConfirmedModificationIntoProject(LlmParseResult? parseResult, string summary)
    {
        if (_projectStore is null)
        {
            return;
        }

        (decimal? W, decimal? H, decimal? T)? upper = null;
        (decimal? W, decimal? H, decimal? T)? lowerChord = null;
        (decimal? W, decimal? H, decimal? T)? lowerTruss = null;

        if (!string.IsNullOrWhiteSpace(_lastSubmittedInput) &&
            _trussMemberCommandParser.TryParse(_lastSubmittedInput, out var trussParsed))
        {
            if (trussParsed.TargetMemberIds.Any(id => string.Equals(id, "upper_chord", StringComparison.OrdinalIgnoreCase)))
            {
                upper = (trussParsed.Width, trussParsed.Height, trussParsed.Thickness);
            }

            if (trussParsed.TargetMemberIds.Any(id => id.StartsWith("lower_chord", StringComparison.OrdinalIgnoreCase)))
            {
                lowerChord = (trussParsed.Width, trussParsed.Height, trussParsed.Thickness);
                if (trussParsed.Width.HasValue && trussParsed.Height.HasValue)
                {
                    lowerTruss = (trussParsed.Width, trussParsed.Height, trussParsed.Thickness);
                }
            }

            if (trussParsed.TargetMemberIds.Any(id => string.Equals(id, "lower_truss", StringComparison.OrdinalIgnoreCase)))
            {
                lowerTruss = (trussParsed.Width, trussParsed.Height, trussParsed.Thickness);
                lowerChord ??= lowerTruss;
            }
        }
        else if (parseResult?.Parameters is { Count: > 0 })
        {
            decimal? uw = null, uh = null, ut = null, lw = null, lh = null, lt = null;
            foreach (var p in parseResult.Parameters)
            {
                var v = TryGetDecimalValue(p.Value);
                if (v is null) continue;
                var n = NormalizeIntentText($"{p.Name}{p.DisplayName}");
                if (n.Contains("上弦") && n.Contains("宽")) uw = v;
                else if (n.Contains("上弦") && n.Contains("高")) uh = v;
                else if (n.Contains("上弦") && n.Contains("厚")) ut = v;
                else if (n.Contains("下弦") && n.Contains("宽")) lw = v;
                else if (n.Contains("下弦") && n.Contains("高")) lh = v;
                else if (n.Contains("下弦") && n.Contains("厚")) lt = v;
            }
            if (uw.HasValue || uh.HasValue || ut.HasValue) upper = (uw, uh, ut);
            if (lw.HasValue || lh.HasValue || lt.HasValue) lowerChord = (lw, lh, lt);
        }

        if (ActiveProject is not { } active)
        {
            return;
        }

        _projectStore.RecordConfirmedModification(active, summary, upper, lowerChord, lowerTruss);
        SyncVersionHistoryCollection();
        RefreshProjectHudProperties();
        RebuildProjectSidebarItems();
        NotifyTaskIsolationStateChanged();
    }

    private string BuildProjectLongTermMemoryPromptBlock()
    {
        if (_projectStore is null || ActiveProject is not { } p)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("【当前持续设计项目长期记忆（跨会话持久保持）】");
        sb.AppendLine($"- 当前项目名称：{p.Name}（当前版本：{p.VersionTag}，累计已确认改模 {p.Metrics.ConfirmedCadModifications} 次）");
        sb.AppendLine($"- 项目设计目标与约束备注：{p.DesignGoalSummary}");
        if (p.VersionHistory.Count > 1)
        {
            var recent = p.VersionHistory.TakeLast(4).Select(v => $"{v.VersionTag}({v.Summary})");
            sb.AppendLine($"- 项目版本演进轨迹：{string.Join(" -> ", recent)}");
        }

        return sb.ToString().TrimEnd();
    }

    private string BuildLiveStructureDimensionsPromptBlock()
    {
        if (_projectStore is null)
        {
            return string.Empty;
        }

        var upper = ActiveProject?.UpperChord ?? MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆");
        var lower = ActiveProject?.LowerChord ?? MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆");
        var sb = new StringBuilder();
        sb.AppendLine("【当前项目结构截面尺寸状态（回答“现在尺寸是多少”等问题时的直接权威依据；注：下弦杆与下桁架为同一组下部主弦杆，对外统一称“下弦杆”）】");
        foreach (var item in new[] { upper, lower })
        {
            sb.AppendLine(
                $"- {item.MemberName}截面尺寸：外宽 {item.WidthMm:0.##} mm × 外高 {item.HeightMm:0.##} mm × 壁厚 {item.ThicknessMm:0.##} mm（来源：{item.Source}）");
        }

        var upperM = upper.Mechanics;
        sb.AppendLine(
            $"- 当前关联几何状态：上弦横向联动红管长度 = {upperM.LinkedUpperRedTubeLengthMm:0.##} mm；中部水平通道有效通行净宽 = {upperM.EffectiveHorizontalWalkwayWidthMm:0.##} mm（名义 1270 mm）。");
        return sb.ToString().TrimEnd();
    }

    private void SyncVersionHistoryCollection()
    {
        ActiveVersionHistory.Clear();
        if (_projectStore is null || ActiveProject is not { } active) return;
        foreach (var cp in active.VersionHistory.AsEnumerable().Reverse())
        {
            ActiveVersionHistory.Add(cp);
        }
    }

    private void RefreshProjectHudProperties()
    {
        OnPropertyChanged(nameof(ActiveProject));
        OnPropertyChanged(nameof(HasActiveProject));
        OnPropertyChanged(nameof(ActiveProjectDisplayName));
        OnPropertyChanged(nameof(HasZeroProjects));
        OnPropertyChanged(nameof(ShowEmptyProjectsState));
        OnPropertyChanged(nameof(EmptyProjectsHintText));
        OnPropertyChanged(nameof(CurrentProjectHeaderBadge));
        OnPropertyChanged(nameof(CurrentProjectGoalText));
        OnPropertyChanged(nameof(CurrentVersionTag));
        OnPropertyChanged(nameof(CurrentUpperChordHud));
        OnPropertyChanged(nameof(CurrentLowerChordHud));
        OnPropertyChanged(nameof(CurrentLowerTrussHud));
        OnPropertyChanged(nameof(CurrentProjectWorkspaceSummary));
        OnPropertyChanged(nameof(CurrentLogScopeSummary));
        RefreshWelcomeDashboard();
    }

    private void RebuildProjectSidebarItems()
    {
        if (_projectStore is null) return;

        var keyword = (_projectSearchKeyword ?? string.Empty).Trim();
        var query = _projectStore.Projects.AsEnumerable();

        if (IsFilterActive)
        {
            query = query.Where(p => !p.IsArchived);
        }
        else if (IsFilterArchived)
        {
            query = query.Where(p => p.IsArchived);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(p =>
                p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                p.VersionBadgeText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                p.Conversations.Any(c => c.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        var ordered = query
            .OrderByDescending(p => p.IsPinned)
            .ThenByDescending(p => p.UpdatedAt)
            .ToList();

        FilteredProjects.Clear();
        var activeId = ActiveProject?.Id;
        foreach (var proj in ordered)
        {
            var isProjActive = !string.IsNullOrWhiteSpace(activeId) &&
                               string.Equals(proj.Id, activeId, StringComparison.OrdinalIgnoreCase);
            var vm = new DesignProjectItemViewModel
            {
                Record = proj,
                IsActive = isProjActive,
                IsExpanded = !_collapsedProjectIds.Contains(proj.Id)
            };

            foreach (var conv in proj.Conversations.OrderByDescending(c => c.UpdatedAt))
            {
                vm.Conversations.Add(new DesignConversationItemViewModel
                {
                    Id = conv.Id,
                    ProjectId = proj.Id,
                    Title = conv.Title,
                    UpdatedAt = conv.UpdatedAt,
                    IsSelected = isProjActive && string.Equals(conv.Id, _activeConversation?.Id, StringComparison.OrdinalIgnoreCase)
                });
            }

            FilteredProjects.Add(vm);
        }

        OnPropertyChanged(nameof(HasActiveProject));
        OnPropertyChanged(nameof(HasZeroProjects));
        OnPropertyChanged(nameof(ShowEmptyProjectsState));
        OnPropertyChanged(nameof(EmptyProjectsHintText));
    }

    private void UpdateSidebarConversationItemInPlace()
    {
        if (ActiveProject is not { } active)
        {
            return;
        }

        var projVm = FilteredProjects.FirstOrDefault(p => string.Equals(p.Id, active.Id, StringComparison.OrdinalIgnoreCase));
        if (projVm is null)
        {
            RebuildProjectSidebarItems();
            return;
        }

        projVm.RefreshMetadata();
        var convVm = projVm.Conversations.FirstOrDefault(c => string.Equals(c.Id, _activeConversation?.Id, StringComparison.OrdinalIgnoreCase));
        if (convVm is not null && _activeConversation is not null)
        {
            convVm.Title = _activeConversation.Title;
            convVm.UpdatedAt = _activeConversation.UpdatedAt;
        }
    }
}
