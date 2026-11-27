using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Chat;
using LlmWpfPrototype.Services.Rag;

namespace LlmWpfPrototype.ViewModels;

public sealed partial class MainViewModel
{
    private const string DefaultConversationTaskKey = "__default_conversation__";
    private readonly object _activeTasksSync = new();
    private readonly Dictionary<string, ActiveConversationTask> _activeTasks = new(StringComparer.OrdinalIgnoreCase);

    internal sealed class ActiveConversationTask
    {
        public required string Key { get; init; }
        public required string ProjectId { get; init; }
        public required string ConversationId { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public bool IsRag { get; set; }
        public bool IsCadExclusive { get; set; }
        public string ProgressText { get; set; } = "正在处理，请稍候...";
        public List<LogEntryItem> BackgroundLogs { get; } = [];
    }

    private string CurrentConversationTaskKey =>
        !string.IsNullOrWhiteSpace(_activeConversation?.Id)
            ? _activeConversation.Id
            : DefaultConversationTaskKey;

    public bool IsCurrentConversationBusy
    {
        get
        {
            lock (_activeTasksSync)
            {
                return _activeTasks.ContainsKey(CurrentConversationTaskKey);
            }
        }
    }

    public bool IsCadExclusiveOperationRunning
    {
        get
        {
            if (_isGeneratingModel)
            {
                return true;
            }

            lock (_activeTasksSync)
            {
                return _activeTasks.Values.Any(t => t.IsCadExclusive);
            }
        }
    }

    /// <summary>
    /// True when the currently displayed conversation has an active task OR a SolidWorks exclusive 3D operation is running.
    /// Switching to a clean new conversation while another conversation runs a pure LLM/RAG request yields IsAssistantBusy = false!
    /// </summary>
    public bool IsAssistantBusy => IsCurrentConversationBusy || IsCadExclusiveOperationRunning;

    /// <summary>
    /// Dedicated live task progress text — strictly decoupled from transient StatusMessage notices
    /// (so messages like "已新建项目 轻量化方案 2" can NEVER appear inside the blue loading progress bar).
    /// </summary>
    public string BusyBannerText
    {
        get
        {
            var currentTask = TryGetActiveTaskForCurrentView();
            if (currentTask is not null)
            {
                if (currentTask.IsRag && !string.IsNullOrWhiteSpace(ChatStatus))
                {
                    return ChatStatus;
                }

                return string.IsNullOrWhiteSpace(currentTask.ProgressText)
                    ? "正在处理，请稍候..."
                    : currentTask.ProgressText;
            }

            var cadTask = TryGetRunningCadExclusiveTask();
            if (cadTask is not null)
            {
                return string.IsNullOrWhiteSpace(cadTask.ProgressText)
                    ? "正在执行 SolidWorks 三维操作，请稍候..."
                    : cadTask.ProgressText;
            }

            if (_isGeneratingModel)
            {
                return "正在执行 SolidWorks 三维操作，请稍候...";
            }

            return "正在处理，请稍候...";
        }
    }

    /// <summary>
    /// True whenever the current view has a cancelable active task (RAG, main LLM chat, or SolidWorks open/rebind).
    /// </summary>
    public bool CanCancelActiveTask => TryGetCancelableTaskForCurrentView() is not null;

    /// <summary>
    /// True when project/conversation/workspace switching is allowed (blocked ONLY during exclusive SolidWorks 3D operations,
    /// NEVER blocked by pure LLM/RAG chat in another conversation).
    /// </summary>
    public bool CanSwitchProjectOrConversation => !IsCadExclusiveOperationRunning;

    private ActiveConversationTask? TryGetActiveTaskForCurrentView()
    {
        lock (_activeTasksSync)
        {
            return _activeTasks.TryGetValue(CurrentConversationTaskKey, out var task) ? task : null;
        }
    }

    private ActiveConversationTask? TryGetRunningCadExclusiveTask()
    {
        lock (_activeTasksSync)
        {
            return _activeTasks.Values.FirstOrDefault(t => t.IsCadExclusive);
        }
    }

    private ActiveConversationTask? TryGetCancelableTaskForCurrentView()
    {
        return TryGetActiveTaskForCurrentView() ?? TryGetRunningCadExclusiveTask();
    }

    private ActiveConversationTask BeginConversationTask(
        string initialProgressText,
        bool isRag = false,
        bool isCadExclusive = false)
    {
        var key = CurrentConversationTaskKey;
        var task = new ActiveConversationTask
        {
            Key = key,
            ProjectId = ActiveProject?.Id ?? string.Empty,
            ConversationId = _activeConversation?.Id ?? string.Empty,
            Cancellation = new CancellationTokenSource(),
            IsRag = isRag,
            IsCadExclusive = isCadExclusive,
            ProgressText = string.IsNullOrWhiteSpace(initialProgressText) ? "正在处理，请稍候..." : initialProgressText
        };

        lock (_activeTasksSync)
        {
            _activeTasks[key] = task;
        }

        NotifyTaskIsolationStateChanged();
        return task;
    }

    private void UpdateTaskProgress(ActiveConversationTask? task, string progressText)
    {
        if (task is null || string.IsNullOrWhiteSpace(progressText))
        {
            return;
        }

        task.ProgressText = progressText.Trim();
        if (CheckAccessToUiThread())
        {
            OnPropertyChanged(nameof(BusyBannerText));
        }
        else
        {
            ExecuteOnUiThread(() => OnPropertyChanged(nameof(BusyBannerText)));
        }
    }

    private bool TryPromoteTaskToCadExclusive(ActiveConversationTask task, string progressText)
    {
        // Guard against race: if the user already switched to another project while the LLM was still thinking,
        // abort before touching the new project's SolidWorks workspace!
        if (!string.IsNullOrWhiteSpace(task.ProjectId) &&
            !string.Equals(task.ProjectId, ActiveProject?.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (_activeTasksSync)
        {
            task.IsCadExclusive = true;
            if (!string.IsNullOrWhiteSpace(progressText))
            {
                task.ProgressText = progressText;
            }
        }

        NotifyTaskIsolationStateChanged();
        return true;
    }

    private void EndConversationTask(ActiveConversationTask? task)
    {
        if (task is null)
        {
            return;
        }

        FlushBackgroundTaskLogsIfNeeded(task);

        lock (_activeTasksSync)
        {
            if (_activeTasks.TryGetValue(task.Key, out var current) && ReferenceEquals(current, task))
            {
                _activeTasks.Remove(task.Key);
            }
        }

        try
        {
            task.Cancellation.Dispose();
        }
        catch
        {
        }

        NotifyTaskIsolationStateChanged();
    }

    private void CancelActiveTaskForCurrentView()
    {
        var task = TryGetCancelableTaskForCurrentView();
        if (task is null)
        {
            _chatCancellation?.Cancel();
            return;
        }

        UpdateTaskProgress(task, "正在停止当前任务，请稍候...");
        try
        {
            if (!task.Cancellation.IsCancellationRequested)
            {
                task.Cancellation.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        _chatCancellation?.Cancel();
        NotifyTaskIsolationStateChanged();
    }

    private bool IsTaskActiveOnUi(ActiveConversationTask? task)
    {
        if (task is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(task.ConversationId))
        {
            return string.IsNullOrWhiteSpace(_activeConversation?.Id);
        }

        return string.Equals(task.ProjectId, ActiveProject?.Id, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(task.ConversationId, _activeConversation?.Id, StringComparison.OrdinalIgnoreCase);
    }

    private void AddAssistantMessageForTask(
        ActiveConversationTask? task,
        string text,
        bool isLocalKnowledge = false,
        IReadOnlyList<LocalChatEvidence>? localSources = null,
        string? localCitationStatus = null,
        string roleDisplay = "AI")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (IsTaskActiveOnUi(task))
        {
            ChatMessages.Add(new ChatMessage
            {
                IsUser = false,
                RoleDisplay = roleDisplay,
                Text = text,
                IsLocalKnowledgeReply = isLocalKnowledge,
                LocalSources = localSources ?? [],
                LocalCitationStatus = localCitationStatus ?? string.Empty
            });
            return;
        }

        if (_projectStore is not null && task is not null)
        {
            var persistedMsg = new PersistedChatMessage
            {
                IsUser = false,
                RoleDisplay = roleDisplay,
                Text = text,
                IsLocalKnowledgeReply = isLocalKnowledge,
                LocalCitationStatus = localCitationStatus ?? string.Empty,
                Timestamp = DateTime.Now
            };
            _projectStore.AppendBackgroundConversationResult(
                task.ProjectId,
                task.ConversationId,
                assistantMessage: persistedMsg);
            RebuildProjectSidebarItems();
        }
    }

    private void RecordConversationTurnForTask(
        ActiveConversationTask? task,
        string userInput,
        string assistantReply,
        bool isKnowledgeTurn = false,
        KnowledgeHistoryProvenance? userProvenance = null,
        KnowledgeHistoryProvenance? answerProvenance = null)
    {
        if (IsTaskActiveOnUi(task))
        {
            if (isKnowledgeTurn)
            {
                _memoryStore.RecordKnowledgeTurn(userInput, assistantReply, userProvenance, answerProvenance);
                PersistActiveConversationSnapshot(userInput);
            }
            else
            {
                AppendConversationHistory(userInput, assistantReply);
            }

            return;
        }

        if (_projectStore is not null && task is not null)
        {
            var persistedTurn = new PersistedTurnRecord
            {
                Timestamp = DateTime.Now,
                Kind = isKnowledgeTurn ? ConversationTurnKind.LocalKnowledge : ConversationTurnKind.Chat,
                UserContent = userInput,
                AssistantContent = assistantReply
            };
            _projectStore.AppendBackgroundConversationResult(
                task.ProjectId,
                task.ConversationId,
                turn: persistedTurn,
                userInputForTitle: userInput);
            RebuildProjectSidebarItems();
        }
    }

    private void AppendLogForTask(
        ActiveConversationTask? task,
        string message,
        Exception? exception = null,
        string source = "MainViewModel")
    {
        if (!string.IsNullOrWhiteSpace(message) &&
            message.StartsWith("[Progress]", StringComparison.OrdinalIgnoreCase))
        {
            var progressDetail = message["[Progress]".Length..].Trim();
            UpdateTaskProgress(task, progressDetail);
            return;
        }

        if (IsTaskActiveOnUi(task))
        {
            AppendLog(message, exception, source);
            return;
        }

        if (task is null)
        {
            AppendLog(message, exception, source);
            return;
        }

        var cleanedMessage = SanitizeLogText(StripLogCategoryPrefix(message));
        var entry = new LogEntryItem
        {
            Timestamp = DateTime.Now,
            Level = ResolveLogLevel(cleanedMessage, exception),
            Category = ResolveLogCategory(message),
            Action = ResolveLogAction(cleanedMessage),
            Message = cleanedMessage,
            Exception = exception?.ToString() ?? string.Empty,
            Source = source
        };

        lock (task.BackgroundLogs)
        {
            task.BackgroundLogs.Add(entry);
        }

        AppendLogToFile(entry);
    }

    private void FlushBackgroundTaskLogsIfNeeded(ActiveConversationTask task)
    {
        if (_projectStore is null || string.IsNullOrWhiteSpace(task.ProjectId) || string.IsNullOrWhiteSpace(task.ConversationId))
        {
            return;
        }

        List<LogEntryItem> snapshot;
        lock (task.BackgroundLogs)
        {
            if (task.BackgroundLogs.Count == 0)
            {
                return;
            }

            snapshot = task.BackgroundLogs.ToList();
            task.BackgroundLogs.Clear();
        }

        _projectStore.AppendBackgroundConversationResult(
            task.ProjectId,
            task.ConversationId,
            extraLogs: snapshot);
    }

    private static bool ContainsCadExclusiveAction(LlmParseResult? parseResult)
    {
        if (parseResult?.Actions is null || parseResult.Actions.Count == 0)
        {
            return false;
        }

        return parseResult.Actions.Any(action =>
            string.Equals(action, "open_working_model", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "reset_model_workspace", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "update_solidworks_dimensions", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "scan_solidworks_dimensions", StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeCadExclusiveProgress(LlmParseResult? parseResult)
    {
        if (parseResult?.Actions is null)
        {
            return "正在执行 SolidWorks 三维操作...";
        }

        if (parseResult.Actions.Any(a => string.Equals(a, "update_solidworks_dimensions", StringComparison.OrdinalIgnoreCase)))
        {
            return "正在校验并写入 SolidWorks 杆件尺寸（三维操作期间暂不可切换项目）...";
        }

        if (parseResult.Actions.Any(a => string.Equals(a, "reset_model_workspace", StringComparison.OrdinalIgnoreCase)))
        {
            return "正在重置工作区并打开初始模型（三维操作期间暂不可切换项目）...";
        }

        if (parseResult.Actions.Any(a => string.Equals(a, "open_working_model", StringComparison.OrdinalIgnoreCase)))
        {
            return "正在准备工作区并打开 SolidWorks 工作模型（三维操作期间暂不可切换项目）...";
        }

        return "正在执行 SolidWorks 三维操作（操作期间暂不可切换项目）...";
    }

    private void NotifyTaskIsolationStateChanged()
    {
        void Raise()
        {
            OnPropertyChanged(nameof(IsCurrentConversationBusy));
            OnPropertyChanged(nameof(IsCadExclusiveOperationRunning));
            OnPropertyChanged(nameof(IsAssistantBusy));
            OnPropertyChanged(nameof(IsChatBusy));
            OnPropertyChanged(nameof(CanCancelActiveTask));
            OnPropertyChanged(nameof(CanSwitchProjectOrConversation));
            OnPropertyChanged(nameof(BusyBannerText));
            NotifyUiBusyAndConfirmationChanged();
            RefreshActionState();
        }

        if (CheckAccessToUiThread())
        {
            Raise();
        }
        else
        {
            ExecuteOnUiThread(Raise);
        }
    }
}
