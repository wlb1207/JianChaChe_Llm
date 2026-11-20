using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;

namespace LlmWpfPrototype.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private enum AssistantWorkflowStage
    {
        None,
        ModelNotOpened,
        ModelOpenedIdle,
        ModificationSucceededUnsaved,
        ModificationFailed,
        CleanupConfirmationPending,
        VisibleWindowCloseConfirmationPending,
        Saved
    }

    [Flags]
    private enum ModifiedDimensionKind
    {
        None = 0,
        SectionSize = 1,
        WallThickness = 2
    }

    private enum TrussRegion
    {
        Unknown,
        UpperTruss,
        LowerTruss,
        Both
    }

    private enum PendingModificationKind
    {
        Unknown,
        SectionSize,
        SectionAndWallThickness,
        WallThickness
    }

    private enum UserIntentKind
    {
        Greeting,
        CapabilityQuestion,
        NextStepQuery,
        OpenModelCommand,
        ClearModificationCommand,
        AmbiguousModificationCommand,
        Unknown
    }

    private const string StatusWaitingInput = "待输入";
    private const string StatusPendingConfirmation = "待确认";
    private const string StatusGenerating = "生成中";
    private const string StatusCompleted = "已完成";
    private const string StatusFailed = "失败";
    private const string LogCategoryAll = "全部";
    private const int MaxConversationHistoryMessages = 6;
    private const int PendingModificationTimeoutMinutes = 5;

    private const string LogLevelAll = "全部";
    private const string LogActionAll = "全部动作";
    private static readonly Regex DeterministicSectionSpecRegex = new(
        @"(?<width>\d+(?:\.\d+)?)\s*[xX×*]\s*(?<height>\d+(?:\.\d+)?)\s*(?:[xX×*]\s*(?<thickness>\d+(?:\.\d+)?))?\s*(?:mm)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DeterministicThicknessRegex = new(
        @"壁厚(?:改成|改为|调整为|采用)?\s*(?<value>\d+(?:\.\d+)?)\s*(?:mm)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PendingSectionSpecRegex = new(
        @"^\s*(?:改成|改为|调整为|变成)?\s*(?<width>\d+(?:\.\d+)?)\s*(?:[xX×*]|\s+)\s*(?<height>\d+(?:\.\d+)?)\s*(?:(?:[xX×*]|\s+)\s*(?<thickness>\d+(?:\.\d+)?))?\s*(?:mm)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILlmService _llmService;
    private readonly ParameterDictionaryService _parameterDictionaryService;
    private readonly ApdlReplacementService _apdlReplacementService;
    private readonly ISolidWorksService _solidWorksService;
    private readonly CommandDispatcher _commandDispatcher;
    private readonly ModelWorkspaceManager _workspaceManager;
    private readonly TrussChordSectionConfigService _trussChordSectionConfigService;
    private readonly EditableTrussMemberCatalogService _editableTrussMemberCatalogService;
    private readonly DimensionScanCatalogService _dimensionScanCatalogService;
    private readonly TrussParameterMappingRecommendationService _trussParameterMappingRecommendationService;
    private readonly TrussMemberCommandParser _trussMemberCommandParser;
    private readonly StringBuilder _logBuilder = new();
    private readonly AsyncRelayCommand _primaryActionCommand;
    private readonly AsyncRelayCommand _clearConversationCommand;
    private readonly AsyncRelayCommand _reparseCommand;
    private readonly AsyncRelayCommand _toggleAdvancedDebugCommand;
    private readonly AsyncRelayCommand _generateApdlCommand;
    private readonly AsyncRelayCommand _openApdlOutputFileCommand;
    private readonly AsyncRelayCommand _refreshMappingOptionsCommand;
    private readonly AsyncRelayCommand _applyRecommendedMappingsCommand;
    private readonly AsyncRelayCommand _validateParameterMappingsCommand;
    private readonly AsyncRelayCommand _saveParameterMappingsCommand;
    private readonly AsyncRelayCommand _openDimensionScanCsvCommand;
    private readonly AsyncRelayCommand _openEditableTrussMembersConfigCommand;
    private readonly AsyncRelayCommand _clearLogsCommand;
    private readonly AsyncRelayCommand _exportLogsCommand;
    private readonly AsyncRelayCommand _copyCurrentLogsCommand;
    private readonly AsyncRelayCommand _openLogFileCommand;
    private readonly AsyncRelayCommand _showErrorsOnlyCommand;
    private readonly AsyncRelayCommand _testLlmConnectionCommand;
    private readonly LlmOptions _options;
    private readonly string _logFilePath = Path.Combine(AppPaths.LogsDirectory, "app.log");
    private readonly List<ConversationMessage> _conversationHistory = [];

    private string _selectedLogLevel = LogLevelAll;
    private string _selectedLogAction = LogActionAll;
    private bool _isLogAutoScrollEnabled = true;
    private string _userInput = string.Empty;
    private string _jsonOutput = string.Empty;
    private string _logs = string.Empty;
    private string _statusMessage = "等待输入设计需求。";
    private string _apdlPreviewContent = "尚未生成 APDL 修改文件。";
    private string _apdlOutputPath = "输出文件路径：尚未生成";
    private string _designStatus = StatusWaitingInput;
    private string _llmRuntimeStatus = "离线";
    private string _lastSubmittedInput = string.Empty;
    private string _selectedLogCategory = LogCategoryAll;
    private string _logSearchKeyword = string.Empty;
    private string _dimensionScanSummary = "当前还没有可用的尺寸扫描结果。";
    private string _mappingGuideText = "当前可编辑参数已根据固定配置完成映射，可直接修改尺寸。";
    private string _mappingValidationSummary = "尚未验证映射。";
    private string? _latestApdlOutputFilePath;
    private bool _isAdvancedDebugExpanded;
    private bool _isGeneratingModel;
    private bool _lastDispatchHandled;
    private bool _lastDispatchSuppressDefaultReply;
    private bool _lastDispatchSucceeded;
    private bool _lastExecutableActionSucceeded;
    private bool _hasQueriedEditableParameters;
    private bool _pendingResetWorkspaceConfirmation;
    private AssistantWorkflowStage _workflowStage = AssistantWorkflowStage.None;
    private string _lastSuccessfulActionSummary = string.Empty;
    private string _lastModifiedParameterSummary = string.Empty;
    private bool _hasUnsavedModelChanges;
    private TrussRegion _lastModifiedRegion = TrussRegion.Unknown;
    private bool _hasModifiedUpperTruss;
    private bool _hasModifiedLowerTruss;
    private ModifiedDimensionKind _lastModifiedDimensionKind = ModifiedDimensionKind.None;
    private bool _hasModifiedSectionSize;
    private bool _hasModifiedWallThickness;
    private int _conversationTurn;
    private PendingModificationContext? _pendingModificationContext;
    private sealed record ReplyOnlyIntentContext(
        UserIntentKind Intent,
        string IntentLabel,
        string? FallbackReply = null);

    public MainViewModel(
        ILlmService llmService,
        ParameterDictionaryService parameterDictionaryService,
        ApdlReplacementService apdlReplacementService,
        ISolidWorksService solidWorksService,
        CommandDispatcher commandDispatcher,
        ModelWorkspaceManager workspaceManager,
        LlmOptions options,
        TrussChordSectionConfigService trussChordSectionConfigService,
        EditableTrussMemberCatalogService editableTrussMemberCatalogService,
        DimensionScanCatalogService dimensionScanCatalogService,
        TrussParameterMappingRecommendationService trussParameterMappingRecommendationService,
        TrussMemberCommandParser trussMemberCommandParser)
    {
        _llmService = llmService;
        _parameterDictionaryService = parameterDictionaryService;
        _apdlReplacementService = apdlReplacementService;
        _solidWorksService = solidWorksService;
        _commandDispatcher = commandDispatcher;
        _workspaceManager = workspaceManager;
        _options = options;
        _trussChordSectionConfigService = trussChordSectionConfigService;
        _editableTrussMemberCatalogService = editableTrussMemberCatalogService;
        _dimensionScanCatalogService = dimensionScanCatalogService;
        _trussParameterMappingRecommendationService = trussParameterMappingRecommendationService;
        _trussMemberCommandParser = trussMemberCommandParser;
        _llmService.DiagnosticLogEmitted += message => AppendLog(message, source: "LlmService");

        ChatMessages = new ObservableCollection<ChatMessage>();
        Parameters = new ObservableCollection<ParameterItem>();
        MissingItems = new ObservableCollection<string>();
        LogEntries = new ObservableCollection<LogEntryItem>();
        LogLevels = new ObservableCollection<string> { LogLevelAll, "Info", "Warning", "Error", "AI", "SolidWorks", "Workspace", "Action", "JSON", "Mapping" };
        LogCategories = new ObservableCollection<string> { LogCategoryAll };
        LogActions = new ObservableCollection<string> { LogActionAll };
        EditableTrussMemberMappings = new ObservableCollection<EditableTrussMemberMappingItemViewModel>();

        _primaryActionCommand = new AsyncRelayCommand(HandlePrimaryActionAsync, CanExecutePrimaryAction);
        _clearConversationCommand = new AsyncRelayCommand(ClearConversationAsync);
        _reparseCommand = new AsyncRelayCommand(ReparseAsync, CanReparse);
        _toggleAdvancedDebugCommand = new AsyncRelayCommand(ToggleAdvancedDebugAsync);
        _generateApdlCommand = new AsyncRelayCommand(GenerateApdlAsync, CanGenerateApdl);
        _openApdlOutputFileCommand = new AsyncRelayCommand(OpenApdlOutputFileAsync, CanOpenApdlOutputFile);
        _refreshMappingOptionsCommand = new AsyncRelayCommand(RefreshMappingOptionsAsync);
        _applyRecommendedMappingsCommand = new AsyncRelayCommand(ApplyRecommendedMappingsAsync, CanApplyRecommendedMappings);
        _validateParameterMappingsCommand = new AsyncRelayCommand(ValidateParameterMappingsAsync, CanValidateParameterMappings);
        _saveParameterMappingsCommand = new AsyncRelayCommand(SaveParameterMappingsAsync, CanSaveParameterMappings);
        _openDimensionScanCsvCommand = new AsyncRelayCommand(OpenDimensionScanCsvAsync, CanOpenDimensionScanCsv);
        _openEditableTrussMembersConfigCommand = new AsyncRelayCommand(OpenEditableTrussMembersConfigAsync, CanOpenEditableTrussMembersConfig);
        _clearLogsCommand = new AsyncRelayCommand(ClearLogsAsync);
        _exportLogsCommand = new AsyncRelayCommand(ExportLogsAsync, CanOperateOnLogs);
        _copyCurrentLogsCommand = new AsyncRelayCommand(CopyCurrentLogsAsync, CanOperateOnLogs);
        _openLogFileCommand = new AsyncRelayCommand(OpenLogFileAsync, CanOpenLogFile);
        _showErrorsOnlyCommand = new AsyncRelayCommand(ShowErrorsOnlyAsync, CanOperateOnLogs);
        _testLlmConnectionCommand = new AsyncRelayCommand(TestLlmConnectionAsync);

        PrimaryActionCommand = _primaryActionCommand;
        ClearConversationCommand = _clearConversationCommand;
        ReparseCommand = _reparseCommand;
        ToggleAdvancedDebugCommand = _toggleAdvancedDebugCommand;
        GenerateApdlCommand = _generateApdlCommand;
        OpenApdlOutputFileCommand = _openApdlOutputFileCommand;
        RefreshMappingOptionsCommand = _refreshMappingOptionsCommand;
        ApplyRecommendedMappingsCommand = _applyRecommendedMappingsCommand;
        ValidateParameterMappingsCommand = _validateParameterMappingsCommand;
        SaveParameterMappingsCommand = _saveParameterMappingsCommand;
        OpenDimensionScanCsvCommand = _openDimensionScanCsvCommand;
        OpenEditableTrussMembersConfigCommand = _openEditableTrussMembersConfigCommand;
        ClearLogsCommand = _clearLogsCommand;
        ExportLogsCommand = _exportLogsCommand;
        CopyCurrentLogsCommand = _copyCurrentLogsCommand;
        OpenLogFileCommand = _openLogFileCommand;
        ShowErrorsOnlyCommand = _showErrorsOnlyCommand;
        TestLlmConnectionCommand = _testLlmConnectionCommand;

        LoadMappingItems();
        RefreshScanState();

        AppendLog($"已加载配置：Mode={_options.Mode}，Provider={_options.Provider}，Model={_options.Model}，ApiBaseUrl={_options.ApiBaseUrl}");
        AppendLog($"参数字典路径：{_parameterDictionaryService.GetDictionaryPath()}");
        AppendLog($"桁架弦杆截面参数配置路径：{_trussChordSectionConfigService.GetConfigPath()}");
        AppendLog($"可编辑桁架弦杆配置路径：{_editableTrussMemberCatalogService.GetConfigPath()}");
        AppendLog($"尺寸扫描结果路径：{_dimensionScanCatalogService.JsonPath}");
        AppendLog("[VersionMarker] MainViewModel next-step guidance v5 loaded");
        AppendLog("[VersionMarker] LinkedRedTubeCompensation runtime v1 loaded");
        AppendLog("[VersionMarker] DeterministicLowerTrussGuard runtime v1 loaded");
        AddAssistantMessage("您好，我是桥梁检查车智能设计助手。您可以让我进行 SolidWorks 模型操作、查询或修改已配置的结构参数，也可以咨询 APDL / ANSYS 分析准备相关内容。请输入您的需求，或输入‘你能做什么’查看功能说明。");
    }

    public ObservableCollection<ChatMessage> ChatMessages { get; }

    public ObservableCollection<ParameterItem> Parameters { get; }

    public ObservableCollection<string> MissingItems { get; }

    public ObservableCollection<LogEntryItem> LogEntries { get; }

    public ObservableCollection<string> LogLevels { get; }

    public ObservableCollection<string> LogCategories { get; }

    public ObservableCollection<string> LogActions { get; }

    public ObservableCollection<EditableTrussMemberMappingItemViewModel> EditableTrussMemberMappings { get; }

    public AsyncRelayCommand PrimaryActionCommand { get; }

    public AsyncRelayCommand ClearConversationCommand { get; }

    public AsyncRelayCommand ReparseCommand { get; }

    public AsyncRelayCommand ToggleAdvancedDebugCommand { get; }

    public AsyncRelayCommand GenerateApdlCommand { get; }

    public AsyncRelayCommand OpenApdlOutputFileCommand { get; }

    public AsyncRelayCommand RefreshMappingOptionsCommand { get; }

    public AsyncRelayCommand ApplyRecommendedMappingsCommand { get; }

    public AsyncRelayCommand ValidateParameterMappingsCommand { get; }

    public AsyncRelayCommand SaveParameterMappingsCommand { get; }

    public AsyncRelayCommand OpenDimensionScanCsvCommand { get; }

    public AsyncRelayCommand OpenEditableTrussMembersConfigCommand { get; }

    public AsyncRelayCommand ClearLogsCommand { get; }

    public AsyncRelayCommand ExportLogsCommand { get; }

    public AsyncRelayCommand CopyCurrentLogsCommand { get; }

    public AsyncRelayCommand OpenLogFileCommand { get; }

    public AsyncRelayCommand ShowErrorsOnlyCommand { get; }

    public AsyncRelayCommand TestLlmConnectionCommand { get; }

    public string UserInput
    {
        get => _userInput;
        set
        {
            if (SetProperty(ref _userInput, value))
            {
                RefreshActionState();
            }
        }
    }

    public string JsonOutput
    {
        get => _jsonOutput;
        private set => SetProperty(ref _jsonOutput, value);
    }

    public string Logs
    {
        get => _logs;
        private set => SetProperty(ref _logs, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ApdlPreviewContent
    {
        get => _apdlPreviewContent;
        private set => SetProperty(ref _apdlPreviewContent, value);
    }

    public string ApdlOutputPath
    {
        get => _apdlOutputPath;
        private set => SetProperty(ref _apdlOutputPath, value);
    }

    public bool IsAdvancedDebugExpanded
    {
        get => _isAdvancedDebugExpanded;
        set
        {
            if (SetProperty(ref _isAdvancedDebugExpanded, value))
            {
                OnPropertyChanged(nameof(AdvancedDebugButtonText));
            }
        }
    }

    public string DesignStatus
    {
        get => _designStatus;
        private set
        {
            if (SetProperty(ref _designStatus, value))
            {
                OnPropertyChanged(nameof(CurrentStatusText));
                OnPropertyChanged(nameof(GenerationHint));
                RefreshActionState();
            }
        }
    }

    public string LlmConfigSummary =>
        $"{ResolveConfigValue(_options.Model, "未配置")} / {_llmRuntimeStatus}";

    public string GptStatusSummary =>
        $"{ResolveConfigValue(_options.Model, "未配置")} / {_llmRuntimeStatus}";

    public string DesignModelType => "桥梁检查车";

    public string CurrentStatusText => DesignStatus;

    public string GenerationHint => DesignStatus == StatusPendingConfirmation
        ? (HasExecutableMappings() ? "可以生成模型" : "已识别参数，当前仅完成参数解析")
        : MissingItems.Count > 0
            ? "仍有参数缺失，需要继续补充"
            : "等待识别设计参数";

    public string MissingItemsSummary => MissingItems.Count > 0
        ? string.Join("。", MissingItems)
        : "无";

    public string OverallTargetSummary
    {
        get
        {
            var hasSolidWorks = Parameters.Any(parameter => parameter.Targets.Any(target => target.Contains("solidworks", StringComparison.OrdinalIgnoreCase)));
            var hasApdl = Parameters.Any(parameter => parameter.Targets.Any(target => target.Contains("apdl", StringComparison.OrdinalIgnoreCase) || target.Contains("mapdl", StringComparison.OrdinalIgnoreCase)));

            return (hasSolidWorks, hasApdl) switch
            {
                (true, true) => "SolidWorks / APDL",
                (true, false) => "SolidWorks",
                (false, true) => "APDL",
                _ => "待识别"
            };
        }
    }

    public string PrimaryActionText
    {
        get
        {
            if (_isGeneratingModel)
            {
                return "生成中...";
            }

            if (DesignStatus != StatusPendingConfirmation)
            {
                return "发送";
            }

            return HasExecutableMappings()
                ? "确认并生成 SolidWorks 模型"
                : "确认参数";
        }
    }

    public string AdvancedDebugButtonText => IsAdvancedDebugExpanded ? "收起高级调试" : "高级调试";

    public string TrussChordSectionConfigPreview => _trussChordSectionConfigService.GetPreviewText();

    public string EditableTrussMembersConfigPreview => _editableTrussMemberCatalogService.GetPreviewText();

    public string SelectedLogCategory
    {
        get => _selectedLogCategory;
        set
        {
            if (SetProperty(ref _selectedLogCategory, value))
            {
                UpdateFilteredLogs();
            }
        }
    }

    public string SelectedLogLevel
    {
        get => _selectedLogLevel;
        set
        {
            if (SetProperty(ref _selectedLogLevel, value))
            {
                UpdateFilteredLogs();
            }
        }
    }

    public string SelectedLogAction
    {
        get => _selectedLogAction;
        set
        {
            if (SetProperty(ref _selectedLogAction, value))
            {
                UpdateFilteredLogs();
            }
        }
    }

    public string LogSearchKeyword
    {
        get => _logSearchKeyword;
        set
        {
            if (SetProperty(ref _logSearchKeyword, value))
            {
                UpdateFilteredLogs();
            }
        }
    }

    public bool IsLogAutoScrollEnabled
    {
        get => _isLogAutoScrollEnabled;
        set => SetProperty(ref _isLogAutoScrollEnabled, value);
    }

    public string DimensionScanSummary
    {
        get => _dimensionScanSummary;
        private set => SetProperty(ref _dimensionScanSummary, value);
    }

    public string MappingGuideText
    {
        get => _mappingGuideText;
        private set => SetProperty(ref _mappingGuideText, value);
    }

    public string MappingValidationSummary
    {
        get => _mappingValidationSummary;
        private set => SetProperty(ref _mappingValidationSummary, value);
    }

    public bool HasDimensionScanResult => _dimensionScanCatalogService.HasLatestScanResult();

    private bool CanExecutePrimaryAction()
    {
        if (_isGeneratingModel)
        {
            return false;
        }

        if (DesignStatus == StatusPendingConfirmation)
        {
            return Parameters.Any();
        }

        return !string.IsNullOrWhiteSpace(UserInput);
    }

    private bool CanReparse()
    {
        return !_isGeneratingModel && ChatMessages.Count > 0;
    }

    private bool CanGenerateApdl()
    {
        return Parameters.Any(parameter =>
            string.Equals(parameter.MappingStatus, "OK", StringComparison.OrdinalIgnoreCase) &&
            parameter.ApdlReplacements.Count > 0);
    }

    private bool CanOpenApdlOutputFile()
    {
        return !string.IsNullOrWhiteSpace(_latestApdlOutputFilePath) && File.Exists(_latestApdlOutputFilePath);
    }

    private bool CanOpenDimensionScanCsv()
    {
        return File.Exists(_dimensionScanCatalogService.CsvPath);
    }

    private bool CanOpenEditableTrussMembersConfig()
    {
        return File.Exists(_editableTrussMemberCatalogService.GetConfigPath());
    }

    private bool CanApplyRecommendedMappings()
    {
        return EditableTrussMemberMappings.Count > 0 && HasDimensionScanResult;
    }

    private bool CanValidateParameterMappings()
    {
        return EditableTrussMemberMappings.Count > 0 && HasDimensionScanResult;
    }

    private bool CanSaveParameterMappings()
    {
        return EditableTrussMemberMappings.Count > 0 && HasDimensionScanResult;
    }

    private static bool IsExplicitManualTrussMappingIntent(string input)
    {
        var normalized = NormalizeIntentText(input);
        return normalized.Contains("手动配置桁架参数映射", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("打开桁架参数映射向导", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("配置上弦杆尺寸", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("配置下弦杆尺寸", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("我要手动选择尺寸", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("从扫描结果里选择尺寸", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanOperateOnLogs()
    {
        return LogEntries.Count > 0;
    }

    private bool CanOpenLogFile()
    {
        return File.Exists(_logFilePath);
    }

    private async Task HandlePrimaryActionAsync()
    {
        if (DesignStatus == StatusPendingConfirmation)
        {
            if (_lastExecutableActionSucceeded && _lastDispatchHandled && _lastDispatchSuppressDefaultReply)
            {
                AppendLog("[Dispatch] HandlePrimaryActionAsync 检测到上一轮命令已真实执行成功，跳过旧 fallback。");
                DesignStatus = StatusCompleted;
                StatusMessage = "上一轮模型修改已完成。";
                RefreshSummaryProperties();
                RefreshActionState();
                return;
            }

            if (!HasExecutableMappings())
            {
                DesignStatus = StatusCompleted;
                StatusMessage = "参数已确认，当前阶段仅保留识别结果，尚未配置真实模型映射。";
                AddAssistantMessage("参数已确认。当前阶段仅完成桁架参数识别，尚未配置真实 SolidWorks 映射，因此不会自动修改模型。");
                RefreshSummaryProperties();
                RefreshActionState();
                return;
            }

            await GenerateMainFlowAsync();
            return;
        }

        await SendUserMessageAsync();
    }

    private async Task SendUserMessageAsync()
    {
        var input = UserInput.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        AddUserMessage(input);
        _lastSubmittedInput = input;
        UserInput = string.Empty;

        ReplyOnlyIntentContext? replyOnlyIntent = null;
        try
        {
            _lastDispatchHandled = false;
            _lastDispatchSuppressDefaultReply = false;
            _lastDispatchSucceeded = false;
            _lastExecutableActionSucceeded = false;
            var normalizedInput = NormalizeIntentText(input);
            var hasExplicitModificationCommand = HasExplicitModificationCommand(input, normalizedInput);
            AppendOpenModelIntentDiagnostics(input, normalizedInput);

            if (IsNegativeOrCancelModificationText(input))
            {
                AppendLog($"[IntentGuard] NegativeOrCancelDetected=True InputCategory=negative_or_cancel InputLength={input.Length}");
                AppendLog("[IntentGuard] BlockedModificationBecauseNegativeOrCancel=True");
                AppendLog("[IntentGuard] ReplyTemplate=CancelModification");
                ClearPendingModificationIntent();
                if (!TryPrepareReplyOnlyLocalIntent(
                        UserIntentKind.Unknown,
                        "CancelModification",
                        "明白，当前不会执行模型修改。我已取消这次未完成的修改意图。",
                        out replyOnlyIntent))
                {
                    return;
                }
            }

            if (IsModificationStatusQueryText(input))
            {
                AppendLog("[IntentGuard] ModificationStatusQuery=True");
                if (!TryPrepareReplyOnlyLocalIntent(
                        UserIntentKind.Unknown,
                        "ModificationStatusQuery",
                        BuildModificationStatusReply(),
                        out replyOnlyIntent))
                {
                    return;
                }
            }

            if (IsModelStatusQuestion(input))
            {
                AppendLog("[IntentGuard] ModelStatusQuestion=True ForceQueryModelStatus=True");
                AppendLog("[IntentGuard] LocalIntentDetected=True");
                AppendLog("[IntentGuard] ReplyOnlyMode=True");
                AppendLog("[IntentGuard] AllowLlmReply=True");
                AppendLog("[IntentGuard] BlockedOpenWorkingModelBecauseQuestion=True");
                AppendLog("[IntentGuard] SuppressActionExecution=True");
                AppendLog("[IntentGuard] AllowedActionGeneration=False");
                replyOnlyIntent ??= new ReplyOnlyIntentContext(
                    UserIntentKind.Unknown,
                    "ModelStatusQuestion",
                    BuildModelStatusReply());
            }

            AppendResetIntentDiagnostics(input);

            var classifiedIntent = ClassifyUserIntent(input);
            if (classifiedIntent is UserIntentKind.Greeting or UserIntentKind.CapabilityQuestion or UserIntentKind.NextStepQuery)
            {
                AppendLog($"[Intent] {classifiedIntent}");
                AppendLog("[IntentGuard] LocalIntentDetected=True");
                AppendLog("[IntentGuard] ReplyOnlyMode=True");
                AppendLog("[IntentGuard] AllowLlmReply=True");
                AppendLog("[IntentGuard] SuppressActionExecution=True");
                AppendLog("[IntentGuard] AllowedActionGeneration=False");
                var localReply = BuildLocalIntentReply(classifiedIntent, input);
                if (!TryPrepareReplyOnlyLocalIntent(
                        classifiedIntent,
                        classifiedIntent.ToString(),
                        localReply,
                        out replyOnlyIntent))
                {
                    return;
                }
            }

            if (IsResetCapabilityQuestion(input))
            {
                AppendLog("[IntentGuard] ResetCapabilityQuestion=True");
                AppendLog("[IntentGuard] ProvideResetUsagePrompt=True");
                AppendLog("[IntentGuard] ActionExecutionBlockedBecauseQuestion=True");
                AppendLog("[IntentGuard] LocalIntentDetected=True");
                AppendLog("[IntentGuard] ReplyOnlyMode=True");
                AppendLog("[IntentGuard] AllowLlmReply=True");
                AppendLog("[IntentGuard] SuppressActionExecution=True");
                AppendLog("[IntentGuard] AllowedActionGeneration=False");
                replyOnlyIntent ??= new ReplyOnlyIntentContext(
                    UserIntentKind.Unknown,
                    "ResetCapabilityQuestion",
                    BuildResetCapabilityReply());
            }

            if (IsExplicitResetAndOpenRequest(input))
            {
                _pendingResetWorkspaceConfirmation = false;
                AppendLog("[IntentGuard] ExplicitResetAndOpenRequest=True");
                var resetParseResult = BuildResetAndOpenParseResult();
                await HandleParsedResultAsync(resetParseResult, string.Empty, input);
                return;
            }

            if (_pendingResetWorkspaceConfirmation && IsResetConfirmationCommand(input))
            {
                _pendingResetWorkspaceConfirmation = false;
                AppendLog("[IntentGuard] ExplicitResetAndOpenRequest=True");
                var resetParseResult = BuildResetAndOpenParseResult();
                await HandleParsedResultAsync(resetParseResult, string.Empty, input);
                return;
            }

            if (IsPossibleResetWorkspaceRequest(input))
            {
                _pendingResetWorkspaceConfirmation = true;
                AppendLog("[IntentGuard] PossibleResetWorkspaceRequest=True");
                AppendLog("[IntentGuard] ResetWorkspaceRequiresConfirmation=True");
                AppendLog("[PendingConfirmation] Created Action=reset_model_workspace_then_open");
                if (!TryPrepareReplyOnlyLocalIntent(
                        UserIntentKind.Unknown,
                        "ResetWorkspaceConfirmationPrompt",
                        "你是想打开一份全新的、未修改过的初始模型吗？如果是，请发送：确认重置并打开初始模型。",
                        out replyOnlyIntent))
                {
                    return;
                }
            }

            if (TryBuildParameterSupplementConsultationReply(input, normalizedInput, out var consultationReply))
            {
                AppendLog("[IntentGuard] ParameterSupplementConsultation=True");
                AppendLog("[IntentGuard] SkipPendingModification=True");
                AppendLog("[IntentGuard] ActionExecutionBlockedBecauseConsultation=True");
                if (!TryPrepareReplyOnlyLocalIntent(
                        UserIntentKind.Unknown,
                        "ParameterSupplementConsultation",
                        consultationReply,
                        out replyOnlyIntent))
                {
                    return;
                }
            }

            var shouldBypassPendingForClarification = ShouldBypassPendingModificationForClarification(
                input,
                normalizedInput,
                hasExplicitModificationCommand);
            ExpirePendingModificationContextIfNeeded();
            if (shouldBypassPendingForClarification)
            {
                AppendLog("[PendingModification] BypassedBecauseClarificationOnly=True");
            }
            else if (await TryHandlePendingModificationContextAsync(input))
            {
                return;
            }

            var isQuestionOnlyInput = IsQuestionOnlyInput(input);
            AppendLog($"[IntentGuard] QuestionOnlyInput={isQuestionOnlyInput}");
            if (isQuestionOnlyInput)
            {
                AppendLog("[IntentGuard] LocalHandled=False AllowLlmReply=True");
                AppendLog("[IntentGuard] ReplyOnlyMode=True");
                AppendLog("[IntentGuard] SuppressActionExecution=True");
                AppendLog("[IntentGuard] AllowedActionGeneration=False");
                replyOnlyIntent ??= new ReplyOnlyIntentContext(
                    UserIntentKind.Unknown,
                    "PureQuestion",
                    BuildQuestionOnlySafetyReply(input));
            }

            StatusMessage = "正在识别设计参数。";
            AppendLog("开始解析设计需求。");
            AppendLog($"[System] UserInput=<redacted> Length={input.Length}");
            var deterministicRouteNormalized = NormalizeIntentText(input);
            var inputContainsLowerTrussKeyword =
                deterministicRouteNormalized.Contains("下桁架", StringComparison.OrdinalIgnoreCase) ||
                deterministicRouteNormalized.Contains("下部桁架", StringComparison.OrdinalIgnoreCase) ||
                deterministicRouteNormalized.Contains("下层桁架", StringComparison.OrdinalIgnoreCase);
            var inputContainsLowerChordKeyword =
                deterministicRouteNormalized.Contains("下弦杆", StringComparison.OrdinalIgnoreCase) ||
                deterministicRouteNormalized.Contains("下弦", StringComparison.OrdinalIgnoreCase) ||
                deterministicRouteNormalized.Contains("底弦", StringComparison.OrdinalIgnoreCase) ||
                deterministicRouteNormalized.Contains("桁架下弦杆", StringComparison.OrdinalIgnoreCase) ||
                deterministicRouteNormalized.Contains("下部弦杆", StringComparison.OrdinalIgnoreCase) ||
                deterministicRouteNormalized.Contains("全部下弦杆", StringComparison.OrdinalIgnoreCase) ||
                deterministicRouteNormalized.Contains("三根下弦杆", StringComparison.OrdinalIgnoreCase);
            AppendLog("[DeterministicRouteProbe] Entered=True");
            AppendLog($"[DeterministicRouteProbe] InputContainsLowerTrussKeyword={inputContainsLowerTrussKeyword}");
            AppendLog($"[DeterministicRouteProbe] InputContainsLowerChordKeyword={inputContainsLowerChordKeyword}");
            AppendLog("[DeterministicRouteProbe] TryLowerTrussBeforeLowerChord=True");
            if (TryBuildDeterministicLowerTrussParseResult(input, out var lowerTrussParseResult))
            {
                if (TryGetMatchedLowerTrussKeyword(deterministicRouteNormalized, out var lowerTrussKeyword))
                {
                    AppendLog($"[DeterministicRouteProbe] LowerTrussMatchedKeyword={lowerTrussKeyword}");
                }

                AppendLog("[DeterministicRouteProbe] TryLowerTrussResult=True");
                AppendLog("[Intent] DeterministicLowerTrussParse=True");
                AppendLog("[Intent] DeterministicLowerTrussLinkedUpdate=True");
                await HandleParsedResultAsync(lowerTrussParseResult, string.Empty, input);
                return;
            }

            AppendLog("[DeterministicRouteProbe] TryLowerTrussResult=False");

            if (TryBuildDeterministicLowerChordParseResult(input, out var lowerChordParseResult))
            {
                AppendLog(IsDeterministicLowerTrussParse(lowerChordParseResult)
                    ? "[Intent] DeterministicLowerTrussParse=True"
                    : "[Intent] DeterministicLowerChordParse=True");
                await HandleParsedResultAsync(lowerChordParseResult, string.Empty, input);
                return;
            }

            if (TryBuildDeterministicUpperChordParseResult(input, out var deterministicParseResult, out var deterministicReply))
            {
                AppendLog("[Intent] DeterministicUpperChordParse=True");
                await HandleParsedResultAsync(deterministicParseResult, string.Empty, input);
                return;
            }

            if (!string.IsNullOrWhiteSpace(deterministicReply))
            {
                AppendLog("[Intent] DeterministicUpperChordParse=ClarificationRequired");
                AppendLog($"[AI Reply] {deterministicReply}");
                AddAssistantMessage(deterministicReply);
                return;
            }

            AppendLog("[Intent] OrdinaryInputRoutedTo=LLM");
            AppendLog(BuildCurrentSessionStateLog(input));

            AppendLog($"[ConversationHistory] BeforeRequest HistoryCount={_conversationHistory.Count}");
            AppendLog($"[ConversationHistory] RequestMessages.Count={_conversationHistory.Count + 2}");
            var requestOptions = replyOnlyIntent is null
                ? null
                : new LlmChatRequestOptions(
                    ReplyOnlyMode: true,
                    IntentLabel: replyOnlyIntent.IntentLabel);
            var json = await _llmService.ParseDesignRequirementAsync(input, _conversationHistory, requestOptions);
            JsonOutput = FormatJson(json);
            _llmRuntimeStatus = "在线";
            OnPropertyChanged(nameof(LlmConfigSummary));
            OnPropertyChanged(nameof(GptStatusSummary));

            var parseResult = DeserializeParseResult(json);
            if (parseResult is null)
            {
                throw new LlmResponseParseException(
                    "智能理解结果解析失败。",
                    TruncateForLog(json));
            }

            await HandleParsedResultAsync(parseResult, json, input, replyOnlyIntent);
        }
        catch (LlmResponseParseException ex)
        {
            if (replyOnlyIntent is not null)
            {
                _llmRuntimeStatus = "异常";
                OnPropertyChanged(nameof(LlmConfigSummary));
                OnPropertyChanged(nameof(GptStatusSummary));
                DesignStatus = StatusWaitingInput;
                StatusMessage = "LLM 回复解析失败，已使用本地安全说明。";
                AppendLog($"[AI] 智能理解结果解析失败：{ex.Message}");
                AppendLog($"[AI] RawResponsePreview={ex.RawResponsePreview}");
                AppendLog("[IntentGuard] FallbackLocalReply=True");
                AppendLog("[IntentGuard] FallbackReason=LLMParseFailed");
                AppendLog("[IntentGuard] LlmLocalReplyFallback=True");
                AppendLog("[IntentGuard] LocalReplyMode=Template");
                var fallbackReply = !string.IsNullOrWhiteSpace(replyOnlyIntent.FallbackReply)
                    ? replyOnlyIntent.FallbackReply
                    : BuildQuestionOnlySafetyReply(input);
                AppendLog($"[AI Reply] {fallbackReply}");
                AddAssistantMessage(fallbackReply);
                AppendConversationHistory(input, fallbackReply);
                Parameters.Clear();
                MissingItems.Clear();
                RefreshSummaryProperties();
                return;
            }

            _llmRuntimeStatus = "异常";
            OnPropertyChanged(nameof(LlmConfigSummary));
            OnPropertyChanged(nameof(GptStatusSummary));
            DesignStatus = StatusFailed;
            StatusMessage = "智能理解结果解析失败，请稍后重试或换一种说法。";
            AppendLog($"[AI] 智能理解结果解析失败：{ex.Message}");
            AppendLog($"[AI] RawResponsePreview={ex.RawResponsePreview}");
            AddAssistantMessage("智能理解结果解析失败，请稍后重试或换一种说法。");
            Parameters.Clear();
            MissingItems.Clear();
            RefreshSummaryProperties();
        }
        catch (Exception ex)
        {
            if (replyOnlyIntent is not null)
            {
                _llmRuntimeStatus = "异常";
                OnPropertyChanged(nameof(LlmConfigSummary));
                OnPropertyChanged(nameof(GptStatusSummary));
                DesignStatus = StatusWaitingInput;
                StatusMessage = "LLM 调用失败，已使用本地安全说明。";
                AppendLog($"[AI] LLM 处理失败：{ex.Message}", ex, "MainViewModel");
                AppendLog("[IntentGuard] FallbackLocalReply=True");
                AppendLog("[IntentGuard] FallbackReason=LLMFailed");
                AppendLog("[IntentGuard] LlmLocalReplyFallback=True");
                AppendLog("[IntentGuard] LocalReplyMode=Template");
                var fallbackReply = !string.IsNullOrWhiteSpace(replyOnlyIntent.FallbackReply)
                    ? replyOnlyIntent.FallbackReply
                    : BuildQuestionOnlySafetyReply(input);
                AppendLog($"[AI Reply] {fallbackReply}");
                AddAssistantMessage(fallbackReply);
                AppendConversationHistory(input, fallbackReply);
                Parameters.Clear();
                MissingItems.Clear();
                RefreshSummaryProperties();
                return;
            }

            _llmRuntimeStatus = "异常";
            OnPropertyChanged(nameof(LlmConfigSummary));
            OnPropertyChanged(nameof(GptStatusSummary));
            DesignStatus = StatusFailed;
            StatusMessage = "智能理解服务调用失败，请检查网络、API Key 或模型配置。";
            AppendLog($"[AI] LLM 处理失败：{ex.Message}", ex, "MainViewModel");
            AddAssistantMessage("智能理解服务调用失败，请检查网络、API Key 或模型配置。");

            Parameters.Clear();
            MissingItems.Clear();
            RefreshSummaryProperties();
        }
    }

    private async Task HandleOpenInitialModelAsync()
    {
        try
        {
            StatusMessage = "正在打开初始检查车模型。";
            AppendLog("收到本地命令：打开初始检查车模型。");

            var success = await _solidWorksService.OpenInitialInspectionCarModelAsync(message => AppendLog(message));
            if (success)
            {
                StatusMessage = "已打开初始检查车模型。";
                AddAssistantMessage("已打开初始检查车模型。");
                return;
            }

            StatusMessage = "打开初始检查车模型失败，请检查 SolidWorks、模型路径和许可证。";
            AddAssistantMessage("打开初始检查车模型失败，请检查 SolidWorks、模型路径和许可证。");
        }
        catch (Exception ex)
        {
            StatusMessage = "打开初始检查车模型失败，请检查 SolidWorks、模型路径和许可证。";
            AppendLog($"打开初始检查车模型失败：{ex.Message}");
            AddAssistantMessage("打开初始检查车模型失败，请检查 SolidWorks、模型路径和许可证。");
        }
    }

    private async Task HandleOpenSolidWorksAsync()
    {
        try
        {
            StatusMessage = "正在打开 SolidWorks。";
            AppendLog("收到本地命令：打开 SolidWorks。");

            var success = await _solidWorksService.EnsureSolidWorksAsync(message => AppendLog(message));
            if (success)
            {
                StatusMessage = "SolidWorks 已打开，可以开始建模。";
                AddAssistantMessage("SolidWorks 已打开，可以开始建模。");
                return;
            }

            StatusMessage = "打开 SolidWorks 失败，请确认本机已安装 SolidWorks，并且许可证可用。";
            AddAssistantMessage("打开 SolidWorks 失败，请确认本机已安装 SolidWorks，并且许可证可用。");
        }
        catch (Exception ex)
        {
            StatusMessage = "打开 SolidWorks 失败，请确认本机已安装 SolidWorks，并且许可证可用。";
            AppendLog($"打开 SolidWorks 失败：{ex.Message}");
            AddAssistantMessage("打开 SolidWorks 失败，请确认本机已安装 SolidWorks，并且许可证可用。");
        }
    }

    private async Task HandleOpenWorkingModelAsync()
    {
        try
        {
            StatusMessage = "正在打开即将修改的新模型。";
            AppendLog("收到本地命令：打开即将修改的新模型。");

            AppendLog($"正在初始化新模型工作区：{_workspaceManager.WorkingModelFolder}");
            await _workspaceManager.EnsureWorkingModelAvailableForOpenAsync(message => AppendLog(message));
            AppendLog("新模型工作区初始化完成，准备打开装配体。");
            var assemblyPath = _workspaceManager.GetWorkingAssemblyPath();
            AppendLog($"即将修改的新模型路径：{assemblyPath}");

            var success = await _solidWorksService.OpenAssemblyAsync(assemblyPath, message => AppendLog(message));
            if (success)
            {
                var activePath = _solidWorksService.GetActiveDocumentPath();
                AppendLog($"[SolidWorks] 实际打开路径：{activePath}");
                _workspaceManager.MarkWorkingModelOpened(assemblyPath, activePath, message => AppendLog(message));
                _workflowStage = AssistantWorkflowStage.ModelOpenedIdle;
                _hasUnsavedModelChanges = false;
                _lastSuccessfulActionSummary = "当前模型已经打开";
                StatusMessage = "已打开桥梁检查车模型。";
                AppendLog("[Intent] OpenModelCommandCompleted=True");
                AddAssistantMessage("""
模型已打开。

您现在可以修改桁架构件参数：
- 桁架上弦杆
- 桁架下弦杆

支持的修改方式：
- 单独修改截面
- 单独修改壁厚
- 同时修改截面和壁厚

示例：
把桁架上弦杆截面改成 80x80
把桁架上弦杆壁厚改成 6
把桁架上弦杆截面改成 80x80，壁厚改成 6
把下弦杆改成 80x80x6
""".Trim());
                return;
            }

            StatusMessage = "打开即将修改的新模型失败，请检查新模型路径、SolidWorks 和许可证状态。";
            AppendLog("[Intent] OpenModelCommandCompleted=False");
            AddAssistantMessage("""
模型未能打开。

原因：
打开即将修改的新模型失败，请检查新模型路径、SolidWorks 和许可证状态。

请确认 SolidWorks 可用，且模型文件路径存在。
""".Trim());
        }
        catch (Exception ex)
        {
            StatusMessage = "打开即将修改的新模型失败，请检查新模型路径、SolidWorks 和许可证状态。";
            AppendLog($"打开即将修改的新模型失败：{ex.Message}");
            AppendLog("[Intent] OpenModelCommandCompleted=False");
            AddAssistantMessage($"""
模型未能打开。

原因：
打开即将修改的新模型失败：{ex.Message}

请确认 SolidWorks 可用，且模型文件路径存在。
""".Trim());
        }
    }

    private async Task HandleParsedResultAsync(
        LlmParseResult parseResult,
        string rawResponse,
        string userInput,
        ReplyOnlyIntentContext? replyOnlyIntent = null)
    {
        if (replyOnlyIntent is not null)
        {
            AppendLog("[IntentGuard] ReplyOnlyMode=True");
            if ((parseResult.Actions?.Count ?? 0) > 0 || (parseResult.Commands?.Count ?? 0) > 0)
            {
                AppendLog("[LLM Chat] StructuredActionsIgnoredBecauseReplyOnly=True");
            }

            parseResult.Actions = [];
            parseResult.Commands = [];
            parseResult.Parameters = [];
            parseResult.NeedConfirmation = false;
            parseResult.Questions = [];
        }

        BindParameters(parseResult);

        var llmReply = ResolveAssistantReply(parseResult, rawResponse);
        if (replyOnlyIntent is not null)
        {
            llmReply = RewriteReplyOnlyAssistantReplyIfNeeded(llmReply, userInput, replyOnlyIntent);
            AppendLog("[IntentGuard] SuppressActionExecution=True");
            AppendLog("[IntentGuard] LocalReplyMode=LlmReplyOnly");
            AppendLog("[IntentGuard] LlmLocalReplyFallback=False");
        }

        if (ShouldBlockDispatchBecauseQuestion(userInput, parseResult))
        {
            AppendLog("[IntentGuard] ActionExecutionBlockedBecauseQuestion=True");
            AppendLog("[IntentGuard] LlmReturnedActionButInputIsQuestion=True");
            foreach (var action in (parseResult.Actions ?? []).Where(action => !string.IsNullOrWhiteSpace(action)))
            {
                AppendLog($"[IntentGuard] BlockedAction={action.Trim()}");
            }

            foreach (var command in (parseResult.Commands ?? []).Where(command => !string.IsNullOrWhiteSpace(command)))
            {
                AppendLog($"[IntentGuard] BlockedAction={command.Trim()}");
            }

            AppendLog("[IntentGuard] SuppressActionExecution=True");
            var questionReply = BuildBlockedActionSafetyReply(parseResult);
            AppendLog("[IntentGuard] AssistantReplyRewrittenBecauseActionBlocked=True");
            AppendLog($"[AI Reply] {questionReply}");
            AddAssistantMessage(questionReply);
            AppendConversationHistory(userInput, questionReply);
            return;
        }

        if (TryBuildDeterministicValidationReply(parseResult, out var validationReply))
        {
            AppendLog($"[AI Reply] {validationReply}");
            AddAssistantMessage(validationReply);
            AppendConversationHistory(userInput, validationReply);
            return;
        }

        if (parseResult.NeedConfirmation)
        {
            AppendLog("[IntentGuard] BlockedModificationBecauseNeedConfirmation=True");
            AppendLog("[Intent] NeedConfirmation=True, skip dispatch");
            var confirmationReply = !string.IsNullOrWhiteSpace(llmReply)
                ? llmReply
                : "当前信息还不够完整，请补充缺少的构件或参数。";
            confirmationReply = RewriteAssistantReplyIfExecutionNotSucceeded(
                confirmationReply,
                userInput,
                parseResult,
                actionRequested: HasDispatchCommands(parseResult),
                handled: false,
                succeeded: false,
                fallbackReply: "当前信息还不够完整，请补充缺少的构件或参数。");
            AppendLog($"[AI Reply] {confirmationReply}");
            AddAssistantMessage(confirmationReply);
            AppendConversationHistory(userInput, confirmationReply);
            return;
        }

        if (!HasDispatchCommands(parseResult))
        {
            if (IsQuestionOnlyInput(userInput))
            {
                AppendLog("[IntentGuard] ActionExecutionBlockedBecauseQuestion=True");
            }

            AppendLog("[IntentGuard] LlmActionsEmpty=True DoNotInferModification=True");
            var safeReply = RewriteAssistantReplyIfExecutionNotSucceeded(
                llmReply,
                userInput,
                parseResult,
                actionRequested: false,
                handled: false,
                succeeded: false,
                fallbackReply: "我没有解析到可执行动作，请换一种说法或提供更明确的参数。");
            if (!string.IsNullOrWhiteSpace(safeReply))
            {
                AppendLog($"[AI Reply] {safeReply}");
                AddAssistantMessage(safeReply);
                AppendConversationHistory(userInput, safeReply);
            }
            else
            {
                const string fallbackReply = "我没有解析到可执行动作，请换一种说法或提供更明确的参数。";
                AppendLog($"[AI Reply] {fallbackReply}");
                AddAssistantMessage(fallbackReply);
                AppendConversationHistory(userInput, fallbackReply);
            }

            return;
        }

        if (IsBlockedModificationDispatch(parseResult))
        {
            AppendLog("[IntentGuard] BlockedModificationBecauseNoCompleteParameters=True");
            var blockedReply = !string.IsNullOrWhiteSpace(llmReply)
                ? llmReply
                : "当前还没有形成完整的修改请求，请补充明确的构件和尺寸参数。";
            blockedReply = RewriteAssistantReplyIfExecutionNotSucceeded(
                blockedReply,
                userInput,
                parseResult,
                actionRequested: true,
                handled: false,
                succeeded: false,
                fallbackReply: "当前还没有形成完整的修改请求，请补充明确的构件和尺寸参数。");
            AppendLog($"[AI Reply] {blockedReply}");
            AddAssistantMessage(blockedReply);
            AppendConversationHistory(userInput, blockedReply);
            return;
        }

        var dispatchResult = await TryDispatchCommandsAsync(parseResult);
        _lastDispatchHandled = dispatchResult.Handled;
        _lastDispatchSuppressDefaultReply = dispatchResult.SuppressDefaultReply;
        _lastDispatchSucceeded = dispatchResult.Succeeded;
        AppendLog($"[Dispatch] Handled={dispatchResult.Handled}, SuppressDefaultReply={dispatchResult.SuppressDefaultReply}, Succeeded={dispatchResult.Succeeded}, AssistantMessages.Count={dispatchResult.AssistantMessages.Count}");
        if (dispatchResult.ScanResult is not null)
        {
            RefreshScanState(dispatchResult.ScanResult);
        }

        var dispatchReply = MergeAssistantMessages(dispatchResult.AssistantMessages);
        var finalReply = BuildFinalAssistantReply(parseResult, dispatchResult, llmReply, dispatchReply);
        finalReply = RewriteAssistantReplyIfExecutionNotSucceeded(
            finalReply,
            userInput,
            parseResult,
            actionRequested: HasDispatchCommands(parseResult),
            handled: dispatchResult.Handled,
            succeeded: dispatchResult.Handled && dispatchResult.Succeeded,
            fallbackReply: dispatchResult.Handled
                ? dispatchReply
                : BuildBlockedActionSafetyReply(parseResult));

        if (!string.IsNullOrWhiteSpace(finalReply))
        {
            AppendLog($"[AI Reply] {finalReply}");
            AddAssistantMessage(finalReply);
            AppendLog("[System] 最终回复如下：");
            AppendConversationHistory(userInput, finalReply);
        }

        if (!dispatchResult.Handled)
        {
            return;
        }

        ClearConversationHistoryIfNeeded(parseResult, dispatchResult);
        _lastExecutableActionSucceeded = dispatchResult.Succeeded;
        UpdateWorkflowStateFromDispatchResult(parseResult, dispatchResult);
        if (dispatchResult.SuppressDefaultReply && DesignStatus == StatusPendingConfirmation)
        {
            AppendLog("[Dispatch] 本轮命令已由 CommandDispatcher 真实处理，重置 PendingConfirmation 状态。");
            DesignStatus = StatusCompleted;
            StatusMessage = dispatchResult.Succeeded ? "模型修改已完成。" : "模型修改执行失败。";
            RefreshSummaryProperties();
            RefreshActionState();
        }
    }

    private async Task GenerateMainFlowAsync()
    {
        _isGeneratingModel = true;
        DesignStatus = StatusGenerating;
        StatusMessage = "正在生成 SolidWorks 模型。";
        RefreshActionState();

        try
        {
            AddAssistantMessage("已确认参数，正在进入 SolidWorks 模型生成流程。");

            await GenerateApdlAsync();

            DesignStatus = StatusCompleted;
            StatusMessage = "SolidWorks 模型生成流程已完成。";
            AddAssistantMessage("本轮对话已完成。当前原型阶段已保留参数识别结果，并同步生成了 APDL 调试产物。");
        }
        catch (Exception ex)
        {
            DesignStatus = StatusFailed;
            StatusMessage = $"生成失败：{ex.Message}";
            AddAssistantMessage($"生成过程失败：{ex.Message}");
        }
        finally
        {
            _isGeneratingModel = false;
            RefreshActionState();
        }
    }

    private async Task ClearConversationAsync()
    {
        ChatMessages.Clear();
        Parameters.Clear();
        MissingItems.Clear();
        await _workspaceManager.StartNewSessionAsync();
        JsonOutput = string.Empty;
        ApdlPreviewContent = "尚未生成 APDL 修改文件。";
        ApdlOutputPath = "输出文件路径：尚未生成";
        StatusMessage = "等待输入设计需求。";
        DesignStatus = StatusWaitingInput;
        _workflowStage = AssistantWorkflowStage.None;
        _lastSuccessfulActionSummary = string.Empty;
        _lastModifiedParameterSummary = string.Empty;
        _hasUnsavedModelChanges = false;
        _lastModifiedRegion = TrussRegion.Unknown;
        _hasModifiedUpperTruss = false;
        _hasModifiedLowerTruss = false;
        _lastModifiedDimensionKind = ModifiedDimensionKind.None;
        _hasModifiedSectionSize = false;
        _hasModifiedWallThickness = false;
        _latestApdlOutputFilePath = null;
        _lastSubmittedInput = string.Empty;
        _lastDispatchHandled = false;
        _lastDispatchSuppressDefaultReply = false;
        _lastDispatchSucceeded = false;
        _lastExecutableActionSucceeded = false;
        _hasQueriedEditableParameters = false;
        _pendingResetWorkspaceConfirmation = false;
        _conversationTurn = 0;
        ClearPendingModificationContext("clear_conversation");
        ClearConversationHistory("clear_conversation");
        AppendLog("已开启新的设计会话，工作区标记已重置为未初始化。");
        AddAssistantMessage("对话已清空。你可以继续让我打开当前模型、查询可编辑参数，或直接告诉我要修改的尺寸值。");
        RefreshSummaryProperties();
        RefreshActionState();
    }

    private void AppendConversationHistory(string userInput, string assistantReply)
    {
        if (!string.IsNullOrWhiteSpace(userInput))
        {
            _conversationHistory.Add(new ConversationMessage
            {
                Role = "user",
                Content = userInput.Trim(),
                CreatedAt = DateTime.Now
            });
        }

        if (!string.IsNullOrWhiteSpace(assistantReply))
        {
            _conversationHistory.Add(new ConversationMessage
            {
                Role = "assistant",
                Content = assistantReply.Trim(),
                CreatedAt = DateTime.Now
            });
        }

        AppendLog("[ConversationHistory] Appended UserAndAssistant=True");
        TrimConversationHistory();
    }

    private bool TryPrepareReplyOnlyLocalIntent(
        UserIntentKind intent,
        string intentLabel,
        string fallbackReply,
        out ReplyOnlyIntentContext? replyOnlyIntent)
    {
        AppendLog($"[IntentGuard] AlwaysUseLlmForLocalReplies={_options.AlwaysUseLlmForLocalReplies}");
        if (!_options.AlwaysUseLlmForLocalReplies)
        {
            AppendLog("[IntentGuard] LocalHandled=True");
            AppendLog("[IntentGuard] SkipLlm=True");
            AppendLog("[IntentGuard] LocalReplyMode=Template");
            AppendLog("[IntentGuard] LlmLocalReplyFallback=False");
            AppendLog($"[AI Reply] {fallbackReply}");
            AddAssistantMessage(fallbackReply);
            AppendConversationHistory(_lastSubmittedInput, fallbackReply);
            replyOnlyIntent = null;
            return false;
        }

        AppendLog("[IntentGuard] LocalHandled=True");
        AppendLog("[IntentGuard] SkipLlm=False");
        AppendLog("[IntentGuard] LocalReplyMode=LlmReplyOnly");
        AppendLog("[IntentGuard] LlmLocalReplyFallback=False");
        AppendLog("[IntentGuard] SuppressActionExecution=True");
        AppendLog("[IntentGuard] AllowedActionGeneration=False");
        AppendLog("[IntentGuard] ReplyOnlyMode=True");
        AppendLog("[IntentGuard] LocalTemplateRetained=True");
        AppendLog($"[IntentGuard] LocalTemplatePreview={TruncateForLog(fallbackReply)}");
        AppendLog("[IntentGuard] ContinueToLlmForNaturalReply=True");
        replyOnlyIntent = new ReplyOnlyIntentContext(intent, intentLabel, fallbackReply);
        return true;
    }

    private void TrimConversationHistory()
    {
        var oldCount = _conversationHistory.Count;
        if (oldCount <= MaxConversationHistoryMessages)
        {
            return;
        }

        var removeCount = oldCount - MaxConversationHistoryMessages;
        _conversationHistory.RemoveRange(0, removeCount);
        AppendLog($"[ConversationHistory] Trimmed OldCount={oldCount} NewCount={_conversationHistory.Count}");
    }

    private void ClearConversationHistory(string reason)
    {
        _conversationHistory.Clear();
        AppendLog($"[ConversationHistory] Cleared Reason={reason}");
    }

    private void ClearConversationHistoryIfNeeded(LlmParseResult? parseResult, CommandDispatchResult dispatchResult)
    {
        if (!dispatchResult.Succeeded || parseResult is null)
        {
            return;
        }

        var actions = parseResult.Actions
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Select(action => action.Trim())
            .ToList();

        if (actions.Any(action => string.Equals(action, "open_working_model", StringComparison.OrdinalIgnoreCase)))
        {
            ClearPendingModificationContext("open_working_model");
            ClearConversationHistory("open_working_model");
            return;
        }

        if (actions.Any(action => string.Equals(action, "reset_model_workspace", StringComparison.OrdinalIgnoreCase)))
        {
            ClearPendingModificationContext("reset_model_workspace");
            ClearConversationHistory("reset_model_workspace");
        }
    }

    private async Task<bool> TryHandlePendingModificationContextAsync(string input)
    {
        var normalized = NormalizeIntentText(input);
        if (ShouldBypassPendingModificationForClarification(
                input,
                normalized,
                HasExplicitModificationCommand(input, normalized)))
        {
            AppendLog("[PendingModification] SkipConsumeBecauseClarificationOnly=True");
            return false;
        }

        if (_pendingModificationContext?.IsActive == true)
        {
            if (string.Equals(_pendingModificationContext.MissingSlot, "size", StringComparison.OrdinalIgnoreCase) &&
                !TryResolvePendingTargetMember(normalized, out _, out _) &&
                TryExtractPendingSectionValues(input, out var pendingWidth, out var pendingHeight, out var pendingThickness))
            {
                _pendingModificationContext.SectionWidth = pendingWidth;
                _pendingModificationContext.SectionHeight = pendingHeight;
                _pendingModificationContext.WallThickness = pendingThickness;
                _pendingModificationContext.MissingSlot = null;
                AppendLog($"[PendingModification] Filled Size={FormatSectionSpec(pendingWidth, pendingHeight, pendingThickness)}");

                if (TryBuildParseResultFromPendingContext(_pendingModificationContext, out var completedParseResult, out var synthesizedInput))
                {
                    _lastSubmittedInput = synthesizedInput;
                    AppendLog("[PendingModification] Completed -> update_solidworks_dimensions");
                    await HandleParsedResultAsync(completedParseResult, string.Empty, input);
                    return true;
                }
            }

            if (string.Equals(_pendingModificationContext.MissingSlot, "member", StringComparison.OrdinalIgnoreCase) &&
                TryResolvePendingTargetMember(normalized, out var targetMember, out var targetMemberDisplayName))
            {
                _pendingModificationContext.TargetMember = targetMember;
                _pendingModificationContext.TargetMemberDisplayName = targetMemberDisplayName;
                _pendingModificationContext.MissingSlot = null;
                AppendLog($"[PendingModification] Filled TargetMember={targetMember}");

                if (TryBuildParseResultFromPendingContext(_pendingModificationContext, out var completedParseResult, out var synthesizedInput))
                {
                    _lastSubmittedInput = synthesizedInput;
                    AppendLog("[PendingModification] Completed -> update_solidworks_dimensions");
                    await HandleParsedResultAsync(completedParseResult, string.Empty, input);
                    return true;
                }
            }
        }

        if (TryCreatePendingContextForMemberWithoutSize(input, normalized, out var memberReply))
        {
            AppendLog($"[AI Reply] {memberReply}");
            AddAssistantMessage(memberReply);
            AppendConversationHistory(input, memberReply);
            return true;
        }

        if (TryCreatePendingContextForThicknessWithoutMember(input, normalized, out var thicknessReply))
        {
            AppendLog($"[AI Reply] {thicknessReply}");
            AddAssistantMessage(thicknessReply);
            AppendConversationHistory(input, thicknessReply);
            return true;
        }

        if (TryCreatePendingContextForSizeWithoutMember(input, normalized, out var sizeReply))
        {
            AppendLog($"[AI Reply] {sizeReply}");
            AddAssistantMessage(sizeReply);
            AppendConversationHistory(input, sizeReply);
            return true;
        }

        return false;
    }

    private bool TryCreatePendingContextForMemberWithoutSize(string input, string normalized, out string reply)
    {
        reply = string.Empty;
        if (!LooksLikeIncompleteModificationIntent(input, normalized) ||
            !TryResolvePendingTargetMember(normalized, out var targetMember, out var targetMemberDisplayName) ||
            ContainsIntentKeyword(normalized, "壁厚", "厚度") ||
            TryExtractSectionValues(input, out _, out _, out _) ||
            TryExtractThicknessValue(input, out _))
        {
            return false;
        }

        _pendingModificationContext = new PendingModificationContext
        {
            IsActive = true,
            TargetMember = targetMember,
            TargetMemberDisplayName = targetMemberDisplayName,
            MissingSlot = "size",
            CreatedAt = DateTime.Now,
            SourceUserText = input
        };

        AppendLog($"[PendingModification] Created TargetMember={targetMember} MissingSlot=size");
        reply = $"要把{targetMemberDisplayName}截面改成多少？可以直接输入类似 80x80x6 的尺寸。";
        return true;
    }

    private bool TryCreatePendingContextForSizeWithoutMember(string input, string normalized, out string reply)
    {
        reply = string.Empty;
        if (TryResolvePendingTargetMember(normalized, out _, out _) ||
            !TryExtractPendingSectionValues(input, out var width, out var height, out var thickness) ||
            !HasExplicitModificationCommand(input, normalized) ||
            ShouldBypassPendingModificationForClarification(input, normalized, hasExplicitModificationCommand: true))
        {
            return false;
        }

        _pendingModificationContext = new PendingModificationContext
        {
            IsActive = true,
            SectionWidth = width,
            SectionHeight = height,
            WallThickness = thickness,
            MissingSlot = "member",
            CreatedAt = DateTime.Now,
            SourceUserText = input
        };

        AppendLog($"[PendingModification] Created Size={FormatSectionSpec(width, height, thickness)} MissingSlot=member");
        reply = $"要把 {FormatSectionSpec(width, height, thickness)} 应用于哪一组杆件？请说明是桁架上弦杆还是桁架下弦杆。";
        return true;
    }

    private bool TryCreatePendingContextForThicknessWithoutMember(string input, string normalized, out string reply)
    {
        reply = string.Empty;
        if (TryResolvePendingTargetMember(normalized, out _, out _) ||
            !HasExplicitModificationCommand(input, normalized) ||
            !TryExtractThicknessValue(input, out var thickness) ||
            DeterministicSectionSpecRegex.IsMatch(input) ||
            TryResolveMostRecentModifiedChordMember(out _, out _))
        {
            return false;
        }

        _pendingModificationContext = new PendingModificationContext
        {
            IsActive = true,
            WallThickness = thickness,
            MissingSlot = "member",
            CreatedAt = DateTime.Now,
            SourceUserText = input
        };

        AppendLog($"[PendingModification] Created WallThickness={thickness.ToString(CultureInfo.InvariantCulture)} MissingSlot=member");
        reply = $"要把壁厚 {thickness.ToString(CultureInfo.InvariantCulture)} 应用于哪一组杆件？请说明是桁架上弦杆还是桁架下弦杆。";
        return true;
    }

    private void ExpirePendingModificationContextIfNeeded()
    {
        if (_pendingModificationContext is null || !_pendingModificationContext.IsActive)
        {
            return;
        }

        if (DateTime.Now - _pendingModificationContext.CreatedAt <= TimeSpan.FromMinutes(PendingModificationTimeoutMinutes))
        {
            return;
        }

        AppendLog("[PendingModification] Expired=True");
        ClearPendingModificationContext("expired");
    }

    private void ClearPendingModificationContext(string reason)
    {
        if (_pendingModificationContext is null)
        {
            return;
        }

        if (string.Equals(reason, "user_cancel", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog("[PendingModification] Cancelled Reason=user_cancel");
        }

        _pendingModificationContext = null;
        AppendLog($"[PendingModification] Cleared Reason={reason}");
    }

    private static bool TryResolvePendingTargetMember(string normalized, out string memberKey, out string displayName)
    {
        memberKey = string.Empty;
        displayName = string.Empty;

        if (ContainsIntentKeyword(normalized, "桁架上弦杆", "上弦杆", "桁架上弦", "上弦"))
        {
            memberKey = "upper_chord";
            displayName = "桁架上弦杆";
            return true;
        }

        if (ContainsIntentKeyword(normalized, "桁架下弦杆", "下弦杆", "桁架下弦", "下弦"))
        {
            memberKey = "lower_chord";
            displayName = "桁架下弦杆";
            return true;
        }

        return false;
    }

    private static bool LooksLikeIncompleteModificationIntent(string input, string normalized)
    {
        if (IsParameterSupplementConsultationOnly(input, normalized) ||
            (IsQuestionOnlyInput(input) && !HasExplicitModificationCommand(input, normalized)))
        {
            return false;
        }

        var hasModifyVerb = ContainsIntentKeyword(normalized, "改成", "改为", "修改为", "调整为", "变成", "把");
        var hasMemberKeyword = ContainsIntentKeyword(normalized, "桁架上弦杆", "桁架下弦杆", "上弦杆", "下弦杆", "上弦", "下弦");
        var hasParameterKeyword = ContainsIntentKeyword(normalized, "截面", "宽高", "尺寸", "壁厚", "厚度");
        var hasExecutableValue = ContainsExecutableValue(input);
        return hasModifyVerb &&
               (hasMemberKeyword || hasParameterKeyword) &&
               !hasExecutableValue;
    }

    private static bool TryExtractPendingSectionValues(
        string input,
        out decimal width,
        out decimal height,
        out decimal? thickness)
    {
        width = 0;
        height = 0;
        thickness = null;

        if (TryExtractSectionValues(input, out width, out height, out thickness))
        {
            return true;
        }

        var match = PendingSectionSpecRegex.Match(input);
        if (!match.Success)
        {
            return false;
        }

        if (!decimal.TryParse(match.Groups["width"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out width) ||
            !decimal.TryParse(match.Groups["height"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out height))
        {
            return false;
        }

        if (match.Groups["thickness"].Success &&
            decimal.TryParse(match.Groups["thickness"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedThickness))
        {
            thickness = parsedThickness;
        }

        return true;
    }

    private static string FormatSectionSpec(decimal width, decimal height, decimal? thickness)
    {
        var widthText = width.ToString(CultureInfo.InvariantCulture);
        var heightText = height.ToString(CultureInfo.InvariantCulture);
        if (!thickness.HasValue)
        {
            return $"{widthText}x{heightText}";
        }

        return $"{widthText}x{heightText}x{thickness.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    private static bool TryBuildParseResultFromPendingContext(
        PendingModificationContext context,
        out LlmParseResult parseResult,
        out string synthesizedInput)
    {
        parseResult = new LlmParseResult();
        synthesizedInput = string.Empty;

        if (string.IsNullOrWhiteSpace(context.TargetMember))
        {
            return false;
        }

        var parameters = new List<LlmParsedParameter>();
        var modificationKind = GetPendingModificationKind(context);
        if (modificationKind == PendingModificationKind.Unknown)
        {
            return false;
        }

        switch (context.TargetMember)
        {
            case "upper_chord":
                if (modificationKind is PendingModificationKind.SectionSize or PendingModificationKind.SectionAndWallThickness)
                {
                    parameters.Add(CreateParsedParameter("truss_top_chord_section_width", "桁架上弦杆截面宽度", context.SectionWidth!.Value));
                    parameters.Add(CreateParsedParameter("truss_top_chord_section_height", "桁架上弦杆截面高度", context.SectionHeight!.Value));
                }

                if (modificationKind is PendingModificationKind.SectionAndWallThickness or PendingModificationKind.WallThickness)
                {
                    parameters.Add(CreateParsedParameter("truss_top_chord_wall_thickness", "桁架上弦杆壁厚", context.WallThickness!.Value));
                }

                break;
            case "lower_chord":
                if (modificationKind is PendingModificationKind.SectionSize or PendingModificationKind.SectionAndWallThickness)
                {
                    parameters.Add(CreateParsedParameter("truss_bottom_chord_section_width", "桁架下弦杆截面宽度", context.SectionWidth!.Value));
                    parameters.Add(CreateParsedParameter("truss_bottom_chord_section_height", "桁架下弦杆截面高度", context.SectionHeight!.Value));
                }

                if (modificationKind is PendingModificationKind.SectionAndWallThickness or PendingModificationKind.WallThickness)
                {
                    parameters.Add(CreateParsedParameter("truss_bottom_chord_wall_thickness", "桁架下弦杆壁厚", context.WallThickness!.Value));
                }

                break;
            default:
                return false;
        }

        synthesizedInput = modificationKind switch
        {
            PendingModificationKind.SectionSize => $"把{context.TargetMemberDisplayName}截面改成 {FormatSectionSpec(context.SectionWidth!.Value, context.SectionHeight!.Value, null)}",
            PendingModificationKind.SectionAndWallThickness => $"把{context.TargetMemberDisplayName}截面改成 {FormatSectionSpec(context.SectionWidth!.Value, context.SectionHeight!.Value, context.WallThickness)}",
            PendingModificationKind.WallThickness => $"把{context.TargetMemberDisplayName}壁厚改成 {context.WallThickness!.Value.ToString(CultureInfo.InvariantCulture)}",
            _ => synthesizedInput
        };
        parseResult = CreateDeterministicParseResult($"已识别为{context.TargetMemberDisplayName}参数修改命令。", parameters);
        return true;
    }

    private static PendingModificationKind GetPendingModificationKind(PendingModificationContext context)
    {
        var hasSectionSize = context.SectionWidth.HasValue && context.SectionHeight.HasValue;
        var hasWallThickness = context.WallThickness.HasValue;
        if (hasSectionSize && hasWallThickness)
        {
            return PendingModificationKind.SectionAndWallThickness;
        }

        if (hasSectionSize)
        {
            return PendingModificationKind.SectionSize;
        }

        if (hasWallThickness)
        {
            return PendingModificationKind.WallThickness;
        }

        return PendingModificationKind.Unknown;
    }

    private async Task ReparseAsync()
    {
        if (_isGeneratingModel)
        {
            return;
        }

        DesignStatus = StatusWaitingInput;
        Parameters.Clear();
        MissingItems.Clear();
        StatusMessage = "已重置识别状态，请重新发送需求。";
        RefreshSummaryProperties();

        var inputToReparse = !string.IsNullOrWhiteSpace(UserInput)
            ? UserInput.Trim()
            : _lastSubmittedInput;

        if (!string.IsNullOrWhiteSpace(inputToReparse))
        {
            UserInput = inputToReparse;
            AddAssistantMessage("正在重新解析最近一次需求。");
            await SendUserMessageAsync();
        }
    }

    private Task ToggleAdvancedDebugAsync()
    {
        IsAdvancedDebugExpanded = !IsAdvancedDebugExpanded;
        return Task.CompletedTask;
    }

    private Task GenerateApdlAsync()
    {
        var logs = _apdlReplacementService.GenerateModifiedFiles(Parameters);
        if (logs.Count == 0)
        {
            AppendLog("未生成 APDL 修改文件：没有可处理的 OK 状态参数。");
            return Task.CompletedTask;
        }

        foreach (var log in logs)
        {
            AppendLog(log.Message);
        }

        var outputFilePath = logs
            .Select(item => item.OutputFilePath)
            .LastOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

        if (!string.IsNullOrWhiteSpace(outputFilePath))
        {
            _latestApdlOutputFilePath = outputFilePath;
            ApdlOutputPath = $"输出文件路径：{outputFilePath}";
            ApdlPreviewContent = File.ReadAllText(outputFilePath);
            AppendLog("已加载 APDL 修改文件预览。");
        }
        else
        {
            ApdlOutputPath = "输出文件路径：未找到生成文件";
            ApdlPreviewContent = "未找到可预览的 APDL 输出文件。";
        }

        _openApdlOutputFileCommand.RaiseCanExecuteChanged();
        _generateApdlCommand.RaiseCanExecuteChanged();
        return Task.CompletedTask;
    }

    private Task OpenApdlOutputFileAsync()
    {
        if (string.IsNullOrWhiteSpace(_latestApdlOutputFilePath) || !File.Exists(_latestApdlOutputFilePath))
        {
            AppendLog("无法打开输出文件：文件不存在。");
            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _latestApdlOutputFilePath,
            UseShellExecute = true
        });
        AppendLog($"已打开输出文件：{_latestApdlOutputFilePath}");
        return Task.CompletedTask;
    }

    private async Task TestLlmConnectionAsync()
    {
        const string testPrompt = "请只回复 pong";

        AppendLog("[LLM Test] 开始测试大模型连接。");
        var result = await _llmService.TestConnectionAsync(testPrompt);

        AppendLog($"[LLM Test] ApiBaseUrl={result.ApiBaseUrl}");
        AppendLog($"[LLM Test] ModelName={result.ModelName}");
        AppendLog($"[LLM Test] ApiKeyExists={result.ApiKeyExists}");
        AppendLog($"[LLM Test] HTTP StatusCode={(result.StatusCode?.ToString() ?? "n/a")}");
        AppendLog($"[LLM Test] ElapsedMilliseconds={(result.ElapsedMilliseconds?.ToString() ?? "n/a")}");
        AppendLog($"[LLM Test] ResponseId={result.ResponseId ?? "n/a"}");
        AppendLog($"[LLM Test] ResponseObject={result.ResponseObject ?? "n/a"}");
        AppendLog($"[LLM Test] ResponseModel={result.ResponseModel ?? "n/a"}");
        AppendLog($"[LLM Test] UsagePromptTokens={(result.UsagePromptTokens?.ToString() ?? "n/a")}");
        AppendLog($"[LLM Test] UsageCompletionTokens={(result.UsageCompletionTokens?.ToString() ?? "n/a")}");
        AppendLog($"[LLM Test] UsageTotalTokens={(result.UsageTotalTokens?.ToString() ?? "n/a")}");
        AppendLog($"[LLM Test] FinishReason={result.FinishReason ?? "n/a"}");
        AppendLog($"[LLM Test] RawAssistantContentPreview={result.RawAssistantContentPreview}");
        AppendLog($"[LLM Test] RawResponsePreview={result.RawResponsePreview}");
        AppendLog($"[LLM Test] ErrorResponsePreview={result.ErrorResponsePreview ?? "n/a"}");
        AppendLog($"[LLM Test] ExceptionType={result.ExceptionType ?? "n/a"}");
        AppendLog($"[LLM Test] ExceptionMessage={result.ExceptionMessage ?? "n/a"}");
        AppendLog($"[LLM Test] InnerExceptionMessage={result.InnerExceptionMessage ?? "n/a"}");

        if (result.Succeeded)
        {
            _llmRuntimeStatus = "在线";
            StatusMessage = "LLM 连接测试成功。";
        }
        else
        {
            _llmRuntimeStatus = "异常";
            StatusMessage = $"LLM 连接测试失败：{result.ExceptionMessage ?? "未知错误"}";
        }

        OnPropertyChanged(nameof(LlmConfigSummary));
        OnPropertyChanged(nameof(GptStatusSummary));
    }

    private Task OpenDimensionScanCsvAsync()
    {
        var path = _dimensionScanCatalogService.CsvPath;
        if (!File.Exists(path))
        {
            AppendLog($"扫描结果 CSV 不存在：{path}");
            MappingValidationSummary = "当前还没有可打开的扫描结果 CSV。";
            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
        AppendLog($"已打开扫描结果 CSV：{path}");
        return Task.CompletedTask;
    }

    private Task OpenEditableTrussMembersConfigAsync()
    {
        var path = _editableTrussMemberCatalogService.GetConfigPath();
        if (!File.Exists(path))
        {
            AppendLog($"editable-truss-members.json 不存在：{path}");
            MappingValidationSummary = "当前无法打开 editable-truss-members.json。";
            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
        AppendLog($"已打开 editable-truss-members.json：{path}");
        return Task.CompletedTask;
    }

    private Task RefreshMappingOptionsAsync()
    {
        RefreshScanState();
        foreach (var item in EditableTrussMemberMappings)
        {
            item.ReloadOptions();
        }

        AppendLog("已刷新参数映射页的扫描结果选项。");
        return Task.CompletedTask;
    }

    private Task SaveParameterMappingsAsync()
    {
        var members = EditableTrussMemberMappings
            .Select(item => item.ToEditableTrussMemberItem())
            .ToList();

        _editableTrussMemberCatalogService.SaveMembers(members);
        _editableTrussMemberCatalogService.Reload();
        AppendLog("已将参数映射页中的绑定结果保存到 editable-truss-members.json。");
        MappingGuideText = "映射已保存。你现在可以再问“我现在可以修改哪些参数？”，或直接输入弦杆截面修改指令。";
        OnPropertyChanged(nameof(EditableTrussMembersConfigPreview));
        return Task.CompletedTask;
    }

    private Task ClearLogsAsync()
    {
        LogEntries.Clear();
        _logBuilder.Clear();
        Logs = string.Empty;
        LogCategories.Clear();
        LogCategories.Add(LogCategoryAll);
        LogActions.Clear();
        LogActions.Add(LogActionAll);
        SelectedLogLevel = LogLevelAll;
        SelectedLogCategory = LogCategoryAll;
        SelectedLogAction = LogActionAll;
        LogSearchKeyword = string.Empty;
        RefreshActionState();
        return Task.CompletedTask;
    }

    private Task ExportLogsAsync()
    {
        var exportDirectory = AppPaths.LogsDirectory;
        var exportPath = Path.Combine(exportDirectory, $"exported-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var content = BuildSanitizedLogTextForExportOrCopy();

        File.WriteAllText(exportPath, content, Encoding.UTF8);
        StatusMessage = "日志已导出。";
        return Task.CompletedTask;
    }

    private async Task CopyCurrentLogsAsync()
    {
        var text = BuildSanitizedLogTextForExportOrCopy();
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (!TryBuildSafeCopyLogText(text, out var safeText))
            {
                AppendLog("[Privacy] CopyLogSensitiveCheck=Failed CopyCancelled=True");
                StatusMessage = "日志仍包含疑似敏感内容，已取消复制。";
                return;
            }

            AppendLog("[Privacy] CopyLogSensitiveCheck=Passed");

            if (await TrySetClipboardTextAsync(safeText))
            {
                AppendLog("[Clipboard] WriteSucceeded=True");
                if (TryValidateClipboardReadback(out var clipboardContentRedacted))
                {
                    AppendLog("[Clipboard] ReadbackAvailable=True");
                    AppendLog($"[Privacy] ClipboardContentRedacted={clipboardContentRedacted}");
                }
                else
                {
                    AppendLog("[Clipboard] ReadbackAvailable=False");
                    AppendLog("[Privacy] ClipboardContentRedacted=Unknown");
                }

                StatusMessage = "当前日志已复制到剪贴板。";
            }
            else
            {
                AppendLog("[Clipboard] WriteSucceeded=False");
                StatusMessage = "剪贴板暂时被占用，请稍后重试。";
            }
        }

        return;
    }

    private Task OpenLogFileAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
        if (!File.Exists(_logFilePath))
        {
            File.WriteAllText(_logFilePath, string.Empty, Encoding.UTF8);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _logFilePath,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    private Task ShowErrorsOnlyAsync()
    {
        SelectedLogLevel = "Error";
        return Task.CompletedTask;
    }

    private Task OpenManualTrussMappingGuideAsync()
    {
        IsAdvancedDebugExpanded = true;
        MappingGuideText = "当前可编辑参数已经由系统固定配置完成映射，无需手动选择零件或尺寸。";
        AddAssistantMessage("""
当前可编辑参数已经由系统固定配置完成映射，无需手动选择零件或尺寸。

当前映射状态：
1. 桁架上弦杆截面宽度：已映射，置信度 100%
2. 桁架上弦杆截面高度：已映射，置信度 100%
3. 桁架下弦杆截面宽度：已映射，置信度 100%
4. 桁架下弦杆截面高度：已映射，置信度 100%
5. 壁厚：已映射，置信度 100%

你可以直接输入要修改的参数，例如：“把下弦杆宽度改成 120”。
""");
        return Task.CompletedTask;
    }

    private Task ApplyRecommendedMappingsAsync()
    {
        if (!_trussParameterMappingRecommendationService.TryBuildConfirmedMappings(out var mappings, out var failureMessage))
        {
            MappingValidationSummary = failureMessage;
            AddAssistantMessage(failureMessage);
            return Task.CompletedTask;
        }

        var mappingById = mappings.ToDictionary(item => item.MemberId, StringComparer.OrdinalIgnoreCase);
        foreach (var item in EditableTrussMemberMappings)
        {
            if (mappingById.TryGetValue(item.Id, out var mapping))
            {
                item.ApplyRecommendedMapping(mapping);
            }
        }

        MappingValidationSummary = "已应用推荐映射，请继续验证或保存。";
        AddAssistantMessage(_trussParameterMappingRecommendationService.BuildConfirmPreviewReply(mappings));
        return Task.CompletedTask;
    }

    private Task ValidateParameterMappingsAsync()
    {
        var errors = new List<string>();
        foreach (var item in EditableTrussMemberMappings)
        {
            var result = item.ValidateMapping();
            if (!result.IsValid)
            {
                errors.AddRange(result.Errors);
            }
        }

        MappingValidationSummary = errors.Count == 0
            ? "映射验证通过。"
            : string.Join(Environment.NewLine, errors);
        return Task.CompletedTask;
    }

    private LlmParseResult? DeserializeParseResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var trimmed = json.Trim();

        try
        {
            return JsonSerializer.Deserialize<LlmParseResult>(trimmed, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            if (LooksLikeJsonPayload(trimmed))
            {
                return null;
            }

            return new LlmParseResult
            {
                Reply = trimmed,
                Message = trimmed,
                AssistantText = trimmed,
                Actions = [],
                Commands = []
            };
        }
    }

    private static string ResolveAssistantReply(LlmParseResult? parseResult, string rawResponse)
    {
        var reply = ResolveAssistantReply(parseResult);
        if (!string.IsNullOrWhiteSpace(reply))
        {
            return reply;
        }

        if (TryExtractAssistantReplyFromJson(rawResponse, out var extractedReply))
        {
            return extractedReply;
        }

        var trimmed = rawResponse?.Trim() ?? string.Empty;
        return LooksLikeJsonPayload(trimmed) ? string.Empty : trimmed;
    }

    private static bool HasDispatchCommands(LlmParseResult? parseResult)
    {
        return parseResult?.Actions.Any(action => !string.IsNullOrWhiteSpace(action)) == true;
    }

    private static bool IsBlockedModificationDispatch(LlmParseResult? parseResult)
    {
        if (parseResult is null)
        {
            return false;
        }

        var hasModificationAction = parseResult.Actions.Any(action =>
            string.Equals(action, "update_solidworks_dimensions", StringComparison.OrdinalIgnoreCase));
        if (!hasModificationAction)
        {
            return false;
        }

        return parseResult.Parameters.Count == 0 ||
               parseResult.Parameters.Any(parameter => string.IsNullOrWhiteSpace(parameter.Name)) ||
               parseResult.Parameters.Any(parameter => parameter.Value is null || parameter.Value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
    }

    private static bool IsNegativeOrCancelModificationText(string text)
    {
        var normalized = NormalizeIntentText(text);
        return normalized.Contains("我没让你修改", StringComparison.Ordinal) ||
               normalized.Contains("我也没让你修改", StringComparison.Ordinal) ||
               normalized.Contains("没让你改", StringComparison.Ordinal) ||
               normalized.Contains("不要修改", StringComparison.Ordinal) ||
               normalized.Contains("别修改", StringComparison.Ordinal) ||
               normalized.Contains("不改了", StringComparison.Ordinal) ||
               normalized.Equals("取消", StringComparison.Ordinal) ||
               normalized.Contains("算了", StringComparison.Ordinal) ||
               normalized.Contains("停止", StringComparison.Ordinal);
    }

    private static bool IsModificationStatusQueryText(string text)
    {
        var normalized = NormalizeIntentText(text);
        return normalized.Contains("你修改了吗", StringComparison.Ordinal) ||
               normalized.Contains("修改了吗", StringComparison.Ordinal) ||
               normalized.Contains("你有修改吗", StringComparison.Ordinal) ||
               normalized.Contains("改了吗", StringComparison.Ordinal);
    }

    private static bool IsModelStatusQuestion(string text)
    {
        var normalized = NormalizeIntentText(text);
        var hasModelKeyword = normalized.Contains("模型", StringComparison.Ordinal) ||
                              normalized.Contains("装配体", StringComparison.Ordinal) ||
                              normalized.Contains("solidworks", StringComparison.OrdinalIgnoreCase);
        if (!hasModelKeyword)
        {
            return false;
        }

        return (normalized.Contains("打开", StringComparison.Ordinal) && normalized.Contains("吗", StringComparison.Ordinal)) ||
               normalized.Contains("有没有", StringComparison.Ordinal) ||
               normalized.Contains("是否", StringComparison.Ordinal) ||
               normalized.Contains("开着吗", StringComparison.Ordinal) ||
               normalized.EndsWith("了吗", StringComparison.Ordinal) ||
               normalized.EndsWith("吗", StringComparison.Ordinal) ||
               normalized.EndsWith("吗？", StringComparison.Ordinal) ||
               normalized.EndsWith("么", StringComparison.Ordinal) ||
               normalized.EndsWith("么？", StringComparison.Ordinal);
    }

    private string BuildModificationStatusReply()
    {
        return string.IsNullOrWhiteSpace(_lastModifiedParameterSummary)
            ? "我还没有成功执行过模型尺寸修改。"
            : $"刚才已经完成了：{_lastModifiedParameterSummary}。";
    }

    private string BuildModelStatusReply()
    {
        var isModelOpened = _solidWorksService.TryGetActiveDocumentInfoWithoutStartingSolidWorks(out var activeDocumentPath);
        if (!isModelOpened)
        {
            return "当前还没有打开工作模型。需要打开时，可以说“打开当前模型”。";
        }

        var activeDocumentName = string.IsNullOrWhiteSpace(activeDocumentPath)
            ? "当前文档"
            : Path.GetFileName(activeDocumentPath);

        if (string.IsNullOrWhiteSpace(_workspaceManager.CurrentAssemblyPath))
        {
            return $"当前模型已打开：{activeDocumentName}。";
        }

        return $"当前模型已打开：{activeDocumentName}。{Environment.NewLine}当前装配体路径：{_workspaceManager.CurrentAssemblyPath}";
    }

    private static LlmParseResult BuildResetAndOpenParseResult()
    {
        return new LlmParseResult
        {
            Reply = string.Empty,
            NeedConfirmation = false,
            Actions = new List<string> { "reset_model_workspace", "open_working_model" },
            Commands = new List<string> { "reset_model_workspace", "open_working_model" }
        };
    }

    private static string BuildResetCapabilityReply()
    {
        return """
可以。

如果你想打开一个全新的、未修改过的初始模型，可以这样说：

“重置模型并打开初始模型”
或
“恢复到未修改的初始模型并打开”

注意：这会关闭当前工作模型，并从初始模板重新复制一份新的工作模型。
确认要执行时，请发送：
“确认重置并打开初始模型”
""".Trim();
    }

    private void ClearPendingModificationIntent()
    {
        ClearPendingModificationContext("user_cancel");
        _pendingResetWorkspaceConfirmation = false;
        Parameters.Clear();
        MissingItems.Clear();
        DesignStatus = StatusWaitingInput;
        StatusMessage = "已取消未完成的修改意图。";
        RefreshSummaryProperties();
        RefreshActionState();
    }

    private static string MergeAssistantReplies(string llmReply, string dispatchReply)
    {
        if (string.IsNullOrWhiteSpace(llmReply))
        {
            return dispatchReply.Trim();
        }

        if (string.IsNullOrWhiteSpace(dispatchReply))
        {
            return llmReply.Trim();
        }

        if (string.Equals(llmReply.Trim(), dispatchReply.Trim(), StringComparison.Ordinal))
        {
            return llmReply.Trim();
        }

        return $"{llmReply.Trim()}{Environment.NewLine}{Environment.NewLine}{dispatchReply.Trim()}";
    }

    private string BuildFinalAssistantReply(
        LlmParseResult? parseResult,
        CommandDispatchResult dispatchResult,
        string llmReply,
        string dispatchReply)
    {
        var actions = parseResult?.Actions ?? [];

        if (actions.Any(action => string.Equals(action, "open_working_model", StringComparison.OrdinalIgnoreCase)))
        {
            return BuildOpenModelUserReply(dispatchResult, dispatchReply);
        }

        if (actions.Any(action => string.Equals(action, "update_solidworks_dimensions", StringComparison.OrdinalIgnoreCase)))
        {
            return BuildDimensionModificationUserReply(parseResult, dispatchResult, dispatchReply);
        }

        if (actions.Any(action => string.Equals(action, "query_editable_parameters", StringComparison.OrdinalIgnoreCase)))
        {
            return BuildEditableParametersUserReply(dispatchReply);
        }

        return MergeAssistantReplies(llmReply, dispatchReply);
    }

    private static string BuildBlockedActionSafetyReply(LlmParseResult? parseResult)
    {
        var actions = parseResult?.Actions ?? [];
        if (actions.Any(action => string.Equals(action, "open_working_model", StringComparison.OrdinalIgnoreCase)))
        {
            return "我理解你可能想查看当前模型。为避免误操作，我还没有执行打开动作。如果你确认要打开，请输入：打开当前模型。";
        }

        if (actions.Any(action => string.Equals(action, "update_solidworks_dimensions", StringComparison.OrdinalIgnoreCase)))
        {
            return "我理解你可能想修改模型参数。为避免误操作，我还没有执行修改。请用明确命令说明要修改的构件和目标尺寸，例如：把桁架上弦杆改成 80x80x6。";
        }

        return "我理解你可能在询问如何操作。为避免误操作，我还没有执行任何动作。如果你确认要执行，请用明确命令重新说明。";
    }

    private string BuildEditableParametersUserReply(string dispatchReply)
    {
        var isModelOpened = IsModelOpenForLocalReply();
        var builder = new StringBuilder();
        if (!isModelOpened)
        {
            builder.AppendLine("当前尚未检测到已打开模型，执行修改前请先打开或确认 SolidWorks 模型。");
            builder.AppendLine();
        }

        var editableParameterLines = BuildEditableParameterLines();
        builder.AppendLine("当前可编辑项：");
        if (editableParameterLines.Count == 0)
        {
            builder.AppendLine("- 当前未读取到可编辑项配置，请检查参数映射配置。");
        }
        else
        {
            foreach (var line in editableParameterLines)
            {
                builder.Append("- ").AppendLine(line);
            }
        }

        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(dispatchReply) && editableParameterLines.Count == 0)
        {
            builder.AppendLine(dispatchReply.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("下一步你可以这样输入：");
        builder.AppendLine("- 把上弦杆截面改成你需要的规格");
        builder.AppendLine("- 把下弦杆壁厚改成指定数值");
        builder.AppendLine();
        builder.AppendLine(isModelOpened
            ? "模型已打开后，可继续直接说明要修改的构件，以及宽度、高度、壁厚或截面规格。"
            : "你可以先参考以上可编辑项；执行修改前，请先打开或确认 SolidWorks 当前模型。");
        return builder.ToString().TrimEnd();
    }

    private List<string> BuildEditableParameterLines()
    {
        var lines = new List<string>();
        foreach (var member in _editableTrussMemberCatalogService.GetEnabledMembers())
        {
            var capabilities = new List<string>();
            if (_editableTrussMemberCatalogService.CanExecuteFullSectionUpdate(member))
            {
                capabilities.Add("截面规格");
            }
            else
            {
                if (_editableTrussMemberCatalogService.CanExecuteWidthUpdate(member))
                {
                    capabilities.Add("宽度");
                }

                if (_editableTrussMemberCatalogService.CanExecuteHeightUpdate(member))
                {
                    capabilities.Add("高度");
                }
            }

            if (_editableTrussMemberCatalogService.CanExecuteThicknessUpdate(member))
            {
                capabilities.Add("壁厚");
            }

            var displayName = string.IsNullOrWhiteSpace(member.Name)
                ? member.Id
                : member.Name.Trim();
            lines.Add(capabilities.Count == 0
                ? displayName
                : $"{displayName}：可修改{string.Join("、", capabilities)}");
        }

        return lines;
    }

    private static string TruncateForLog(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= 500 ? value : value[..500];
    }

    private async Task<CommandDispatchResult> TryDispatchCommandsAsync(LlmParseResult? parseResult)
    {
        if (parseResult is null)
        {
            return CommandDispatchResult.NotHandled;
        }

        return await _commandDispatcher.ExecuteAsync(parseResult, Parameters.ToList(), _lastSubmittedInput, message => AppendLog(message));
    }

    private void BindParameters(LlmParseResult? parseResult)
    {
        Parameters.Clear();
        MissingItems.Clear();

        if (parseResult is null)
        {
            DesignStatus = StatusFailed;
            StatusMessage = "模型返回的 JSON 无法解析。";
            return;
        }

        var mappedParameters = parseResult.Parameters.Select(MapToParameterItem).ToList();
        var enrichedParameters = _parameterDictionaryService.EnrichParameters(mappedParameters);
        foreach (var item in enrichedParameters)
        {
            Parameters.Add(item);
            AppendLog($"参数 {item.Name} 的映射状态为 {item.MappingStatus}");
        }

        foreach (var question in parseResult.Questions)
        {
            MissingItems.Add(question);
            AppendLog($"待确认问题：{question}");
        }

        if (parseResult.NeedConfirmation || Parameters.Count == 0 || Parameters.Any(parameter => string.IsNullOrWhiteSpace(parameter.Value)))
        {
            DesignStatus = StatusWaitingInput;
            StatusMessage = MissingItems.Count > 0 ? "参数尚不完整，请继续补充。" : "尚未识别到完整参数。";
        }
        else
        {
            DesignStatus = StatusPendingConfirmation;
            StatusMessage = HasExecutableMappings()
                ? "参数已识别完成，等待你确认后生成 SolidWorks 模型。"
                : "参数已识别完成，当前阶段仅保留桁架参数识别结果。";
        }

        AppendLog($"动作列表：{(parseResult.Actions.Count == 0 ? "无" : string.Join("、", parseResult.Actions))}");
        RefreshSummaryProperties();
        RefreshActionState();
    }

    private static string ResolveAssistantReply(LlmParseResult? parseResult)
    {
        if (parseResult is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(parseResult.Reply))
        {
            return parseResult.Reply.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parseResult.Message))
        {
            return parseResult.Message.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parseResult.AssistantText))
        {
            return parseResult.AssistantText.Trim();
        }

        if (parseResult.Questions.Count > 0)
        {
            return string.Join(Environment.NewLine, parseResult.Questions.Where(item => !string.IsNullOrWhiteSpace(item)));
        }

        return string.Empty;
    }

    private string RewriteReplyOnlyAssistantReplyIfNeeded(
        string llmReply,
        string userInput,
        ReplyOnlyIntentContext replyOnlyIntent)
    {
        return RewriteAssistantReplyIfExecutionNotSucceeded(
            llmReply,
            userInput,
            parseResult: null,
            actionRequested: false,
            handled: false,
            succeeded: false,
            fallbackReply: !string.IsNullOrWhiteSpace(replyOnlyIntent.FallbackReply)
                ? replyOnlyIntent.FallbackReply
                : BuildQuestionOnlySafetyReply(userInput));
    }

    private string RewriteAssistantReplyIfExecutionNotSucceeded(
        string reply,
        string userInput,
        LlmParseResult? parseResult,
        bool actionRequested,
        bool handled,
        bool succeeded,
        string? fallbackReply = null)
    {
        if (succeeded)
        {
            return reply;
        }

        if (!string.IsNullOrWhiteSpace(reply) && !ContainsUnsafeSuccessClaim(reply))
        {
            return reply;
        }

        AppendLog("[IntentGuard] FallbackLocalReply=True");
        AppendLog(string.IsNullOrWhiteSpace(reply)
            ? "[IntentGuard] FallbackReason=LLMFailed"
            : "[IntentGuard] FallbackReason=UnsafeLlmReply");
        AppendLog(handled
            ? "[IntentGuard] ActionExecutionFailedOrCancelled=True"
            : actionRequested
                ? "[IntentGuard] ActionExecutionSkipped=True"
                : "[IntentGuard] ActionExecutionNotRequested=True");

        if (!string.IsNullOrWhiteSpace(fallbackReply))
        {
            return fallbackReply;
        }

        if (actionRequested)
        {
            return BuildBlockedActionSafetyReply(parseResult);
        }

        return BuildQuestionOnlySafetyReply(userInput);
    }

    private static bool ContainsUnsafeSuccessClaim(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        return reply.Contains("已打开", StringComparison.Ordinal) ||
               reply.Contains("已经打开", StringComparison.Ordinal) ||
               reply.Contains("已为你打开", StringComparison.Ordinal) ||
               reply.Contains("模型已打开", StringComparison.Ordinal) ||
               reply.Contains("已执行", StringComparison.Ordinal) ||
               reply.Contains("已经执行", StringComparison.Ordinal) ||
               reply.Contains("执行完成", StringComparison.Ordinal) ||
               reply.Contains("已完成", StringComparison.Ordinal) ||
               reply.Contains("已经完成", StringComparison.Ordinal) ||
               reply.Contains("已处理", StringComparison.Ordinal) ||
               reply.Contains("处理完成", StringComparison.Ordinal) ||
               reply.Contains("已更新", StringComparison.Ordinal) ||
               reply.Contains("更新完成", StringComparison.Ordinal) ||
               reply.Contains("已完成修改", StringComparison.Ordinal) ||
               reply.Contains("已经修改", StringComparison.Ordinal) ||
               reply.Contains("修改完成", StringComparison.Ordinal) ||
               reply.Contains("已保存", StringComparison.Ordinal) ||
               reply.Contains("已经保存", StringComparison.Ordinal) ||
               reply.Contains("已帮你", StringComparison.Ordinal) ||
               reply.Contains("操作完成", StringComparison.Ordinal);
    }

    private static string BuildQuestionOnlySafetyReply(string input)
    {
        var normalized = NormalizeIntentText(input);
        if (IsOpenModelCapabilityQuestion(input, normalized))
        {
            return "可以帮助打开当前工作模型。如果你要执行打开，请直接输入：打开当前模型。";
        }

        return "我可以先为你解释当前能力和操作方式；如果你要执行模型操作，请直接给出明确命令。";
    }

    private bool TryBuildDeterministicUpperChordParseResult(
        string input,
        out LlmParseResult parseResult,
        out string clarificationReply)
    {
        parseResult = new LlmParseResult();
        clarificationReply = string.Empty;

        var normalized = NormalizeIntentText(input);
        var explicitlyMentionsUpperChord = MentionsUpperChord(normalized);
        var explicitlyMentionsLowerChord = MentionsLowerChord(normalized);
        var hasContextualThickness = TryExtractThicknessValue(input, out var contextualThickness);
        var thicknessOnlyWithoutMember = !explicitlyMentionsUpperChord &&
                                         !explicitlyMentionsLowerChord &&
                                         hasContextualThickness &&
                                         !DeterministicSectionSpecRegex.IsMatch(input);

        if (!explicitlyMentionsUpperChord && !thicknessOnlyWithoutMember)
        {
            return false;
        }

        var resolvedToUpperChord = explicitlyMentionsUpperChord;
        var resolvedMemberKey = "upper_chord";
        var resolvedMemberDisplayName = "桁架上弦杆";
        if (explicitlyMentionsLowerChord)
        {
            resolvedMemberKey = "lower_chord";
            resolvedMemberDisplayName = "桁架下弦杆";
        }

        if (!resolvedToUpperChord && thicknessOnlyWithoutMember)
        {
            if (TryResolveMostRecentModifiedChordMember(out resolvedMemberKey, out resolvedMemberDisplayName))
            {
                resolvedToUpperChord = string.Equals(resolvedMemberKey, "upper_chord", StringComparison.OrdinalIgnoreCase);
                AppendLog($"[Intent] ImplicitWallThicknessResolvedTo={resolvedMemberKey}");
            }
            else
            {
                return false;
            }
        }

        if (!resolvedToUpperChord && !string.Equals(resolvedMemberKey, "lower_chord", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parameters = new List<LlmParsedParameter>();
        if (TryExtractSectionValues(input, out var width, out var height, out var thickness))
        {
            parameters.Add(CreateParsedParameter(
                string.Equals(resolvedMemberKey, "lower_chord", StringComparison.OrdinalIgnoreCase)
                    ? "truss_bottom_chord_section_width"
                    : "truss_top_chord_section_width",
                $"{resolvedMemberDisplayName}截面宽度",
                width));
            parameters.Add(CreateParsedParameter(
                string.Equals(resolvedMemberKey, "lower_chord", StringComparison.OrdinalIgnoreCase)
                    ? "truss_bottom_chord_section_height"
                    : "truss_top_chord_section_height",
                $"{resolvedMemberDisplayName}截面高度",
                height));
            if (thickness.HasValue)
            {
                parameters.Add(CreateParsedParameter(
                    string.Equals(resolvedMemberKey, "lower_chord", StringComparison.OrdinalIgnoreCase)
                        ? "truss_bottom_chord_wall_thickness"
                        : "truss_top_chord_wall_thickness",
                    $"{resolvedMemberDisplayName}壁厚",
                    thickness.Value));
            }
            else if (TryExtractThicknessValue(input, out var supplementalThickness))
            {
                parameters.Add(CreateParsedParameter(
                    string.Equals(resolvedMemberKey, "lower_chord", StringComparison.OrdinalIgnoreCase)
                        ? "truss_bottom_chord_wall_thickness"
                        : "truss_top_chord_wall_thickness",
                    $"{resolvedMemberDisplayName}壁厚",
                    supplementalThickness));
            }
        }
        else if (TryExtractThicknessValue(input, out var thicknessOnly))
        {
            parameters.Add(CreateParsedParameter(
                string.Equals(resolvedMemberKey, "lower_chord", StringComparison.OrdinalIgnoreCase)
                    ? "truss_bottom_chord_wall_thickness"
                    : "truss_top_chord_wall_thickness",
                $"{resolvedMemberDisplayName}壁厚",
                thicknessOnly));
        }

        if (parameters.Count == 0)
        {
            return false;
        }

        parseResult = CreateDeterministicParseResult($"已识别为{resolvedMemberDisplayName}参数修改命令。", parameters);
        return true;
    }

    private bool TryBuildDeterministicLowerChordParseResult(
        string input,
        out LlmParseResult parseResult)
    {
        parseResult = new LlmParseResult();

        if (!_trussMemberCommandParser.TryParse(input, out var trussParseResult))
        {
            return false;
        }

        if (trussParseResult.TargetMemberIds.Count == 0 ||
            trussParseResult.TargetMemberIds.Any(memberId =>
                !memberId.StartsWith("lower_chord", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(memberId, EditableTrussMemberCatalogService.LowerTrussMemberId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var isDedicatedLowerTruss = trussParseResult.TargetMemberIds.All(memberId =>
            string.Equals(memberId, EditableTrussMemberCatalogService.LowerTrussMemberId, StringComparison.OrdinalIgnoreCase));
        var targetDisplayName = isDedicatedLowerTruss ? "下桁架" : "桁架下弦杆";

        var parameters = new List<LlmParsedParameter>();
        if (trussParseResult.Width.HasValue)
        {
            parameters.Add(CreateParsedParameter(
                isDedicatedLowerTruss
                    ? "lower_truss_section_width"
                    : "truss_bottom_chord_section_width",
                $"{targetDisplayName}截面宽度",
                trussParseResult.Width.Value));
        }

        if (trussParseResult.Height.HasValue)
        {
            parameters.Add(CreateParsedParameter(
                isDedicatedLowerTruss
                    ? "lower_truss_section_height"
                    : "truss_bottom_chord_section_height",
                $"{targetDisplayName}截面高度",
                trussParseResult.Height.Value));
        }

        if (trussParseResult.Thickness.HasValue)
        {
            parameters.Add(CreateParsedParameter(
                isDedicatedLowerTruss
                    ? "lower_truss_wall_thickness"
                    : "truss_bottom_chord_wall_thickness",
                $"{targetDisplayName}壁厚",
                trussParseResult.Thickness.Value));
        }

        if (parameters.Count == 0)
        {
            return false;
        }

        parseResult = CreateDeterministicParseResult(
            isDedicatedLowerTruss ? "已识别为下桁架参数修改命令。" : "已识别为桁架下弦杆参数修改命令。",
            parameters);
        return true;
    }

    private bool TryBuildDeterministicLowerTrussParseResult(
        string input,
        out LlmParseResult parseResult)
    {
        parseResult = new LlmParseResult();

        var normalized = NormalizeIntentText(input);
        if (!IsDedicatedLowerTrussIntent(normalized))
        {
            return false;
        }

        if (!TryExtractSectionValues(input, out var width, out var height, out var thickness))
        {
            return false;
        }

        decimal effectiveThickness;
        if (thickness.HasValue)
        {
            effectiveThickness = thickness.Value;
        }
        else if (TryExtractThicknessValue(input, out var supplementalThickness))
        {
            effectiveThickness = supplementalThickness;
        }
        else
        {
            return false;
        }

        var parameters = new List<LlmParsedParameter>
        {
            CreateParsedParameter("lower_truss_section_width", "下桁架截面宽度", width),
            CreateParsedParameter("lower_truss_section_height", "下桁架截面高度", height),
            CreateParsedParameter("lower_truss_wall_thickness", "下桁架壁厚", effectiveThickness)
        };

        parseResult = CreateDeterministicParseResult("已识别为下桁架参数修改命令。", parameters);
        return true;
    }

    private static bool IsDeterministicLowerTrussParse(LlmParseResult parseResult)
    {
        return parseResult.Parameters.Any(parameter =>
            string.Equals(parameter.Name, "lower_truss_section_width", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parameter.Name, "lower_truss_section_height", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parameter.Name, "lower_truss_wall_thickness", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDedicatedLowerTrussIntent(string normalized)
    {
        var mentionsLowerTruss =
            normalized.Contains("下桁架", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("下部桁架", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("下层桁架", StringComparison.OrdinalIgnoreCase);

        if (!mentionsLowerTruss)
        {
            return false;
        }

        return !normalized.Contains("下弦杆", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("下弦", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("底弦", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("桁架下弦杆", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("下部弦杆", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("全部下弦杆", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("三根下弦杆", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetMatchedLowerTrussKeyword(string normalized, out string keyword)
    {
        foreach (var candidate in new[] { "下桁架", "下部桁架", "下层桁架" })
        {
            if (normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                keyword = candidate;
                return true;
            }
        }

        keyword = string.Empty;
        return false;
    }

    private static bool TryExtractSectionValues(
        string input,
        out decimal width,
        out decimal height,
        out decimal? thickness)
    {
        width = 0;
        height = 0;
        thickness = null;

        var match = DeterministicSectionSpecRegex.Match(input);
        if (!match.Success)
        {
            return false;
        }

        if (!decimal.TryParse(match.Groups["width"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out width) ||
            !decimal.TryParse(match.Groups["height"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out height))
        {
            return false;
        }

        if (match.Groups["thickness"].Success &&
            decimal.TryParse(match.Groups["thickness"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedThickness))
        {
            thickness = parsedThickness;
        }

        return true;
    }

    private static bool TryExtractThicknessValue(string input, out decimal thickness)
    {
        thickness = 0;
        var match = DeterministicThicknessRegex.Match(input);
        return match.Success &&
               decimal.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out thickness);
    }

    private static bool MentionsUpperChord(string normalizedInput)
    {
        return normalizedInput.Contains("桁架上弦杆", StringComparison.Ordinal) ||
               normalizedInput.Contains("上弦杆", StringComparison.Ordinal) ||
               normalizedInput.Contains("桁架上弦", StringComparison.Ordinal) ||
               normalizedInput.Contains("上弦方管", StringComparison.Ordinal);
    }

    private static bool MentionsLowerChord(string normalizedInput)
    {
        return normalizedInput.Contains("桁架下弦杆", StringComparison.Ordinal) ||
               normalizedInput.Contains("下弦杆", StringComparison.Ordinal) ||
               normalizedInput.Contains("桁架下弦", StringComparison.Ordinal) ||
               normalizedInput.Contains("下弦方管", StringComparison.Ordinal);
    }

    private bool ShouldResolveImplicitThicknessToUpperChord()
    {
        if (_lastModifiedRegion == TrussRegion.UpperTruss && _lastModifiedRegion != TrussRegion.LowerTruss)
        {
            return true;
        }

        return _hasModifiedUpperTruss &&
               !_hasModifiedLowerTruss &&
               _lastModifiedDimensionKind.HasFlag(ModifiedDimensionKind.SectionSize | ModifiedDimensionKind.WallThickness);
    }

    private bool TryResolveMostRecentModifiedChordMember(out string memberKey, out string displayName)
    {
        memberKey = string.Empty;
        displayName = string.Empty;

        switch (_lastModifiedRegion)
        {
            case TrussRegion.UpperTruss:
                memberKey = "upper_chord";
                displayName = "桁架上弦杆";
                return true;
            case TrussRegion.LowerTruss:
                memberKey = "lower_chord";
                displayName = "桁架下弦杆";
                return true;
            default:
                return false;
        }
    }

    private string? TryResolveGlobalWallThicknessParameterName()
    {
        try
        {
            var definition = _parameterDictionaryService.LoadDefinitions().FirstOrDefault(item =>
                string.Equals(item.Name, "wall_thickness", StringComparison.OrdinalIgnoreCase));
            return definition?.Name;
        }
        catch
        {
            return null;
        }
    }

    private static LlmParseResult CreateDeterministicParseResult(string reply, IReadOnlyList<LlmParsedParameter> parameters)
    {
        return new LlmParseResult
        {
            Reply = reply,
            Message = reply,
            AssistantText = reply,
            NeedConfirmation = false,
            Actions = ["update_solidworks_dimensions"],
            Commands = ["update_solidworks_dimensions"],
            Parameters = parameters.ToList()
        };
    }

    private static LlmParsedParameter CreateParsedParameter(string name, string displayName, decimal value)
    {
        return new LlmParsedParameter
        {
            Name = name,
            DisplayName = displayName,
            Value = CreateNumericJsonElement(value),
            Unit = "mm",
            Confidence = 1.0,
            Target = ["solidworks"]
        };
    }

    private static JsonElement CreateNumericJsonElement(decimal value)
    {
        using var document = JsonDocument.Parse(value.ToString(CultureInfo.InvariantCulture));
        return document.RootElement.Clone();
    }

    private static bool TryBuildDeterministicValidationReply(LlmParseResult parseResult, out string reply)
    {
        reply = string.Empty;
        var width = TryGetDeterministicParameterValue(parseResult, "truss_top_chord_section_width");
        var height = TryGetDeterministicParameterValue(parseResult, "truss_top_chord_section_height");
        var thickness = TryGetDeterministicParameterValue(parseResult, "truss_top_chord_wall_thickness");
        if (!thickness.HasValue)
        {
            return false;
        }

        if (thickness > 12m)
        {
            reply = "壁厚数值过大，已取消修改。请确认截面尺寸和壁厚。";
            return true;
        }

        if ((width.HasValue && width.Value - (2m * thickness.Value) <= 0m) ||
            (height.HasValue && height.Value - (2m * thickness.Value) <= 0m))
        {
            reply = "壁厚数值过大，已取消修改。请确认截面尺寸和壁厚。";
            return true;
        }

        return false;
    }

    private static decimal? TryGetDeterministicParameterValue(LlmParseResult parseResult, string parameterName)
    {
        var parameter = parseResult.Parameters.FirstOrDefault(item =>
            string.Equals(item.Name, parameterName, StringComparison.OrdinalIgnoreCase));
        return parameter is null ? null : TryGetDecimalValue(parameter.Value);
    }

    private static bool TryExtractAssistantReplyFromJson(string rawResponse, out string reply)
    {
        reply = string.Empty;
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;
            foreach (var propertyName in new[] { "reply", "message", "assistantText" })
            {
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty(propertyName, out var property) &&
                    property.ValueKind == JsonValueKind.String)
                {
                    var value = property.GetString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        reply = value;
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool LooksLikeJsonPayload(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal) ||
               trimmed.StartsWith("[", StringComparison.Ordinal) ||
               trimmed.StartsWith("```", StringComparison.Ordinal);
    }

    private void BuildAssistantReply()
    {
        if (DesignStatus == StatusPendingConfirmation)
        {
            var summary = Parameters.Count == 0
                ? "暂未识别到参数。"
                : string.Join("。", Parameters.Select(parameter =>
                    $"{ResolveParameterDisplayName(parameter)} = {parameter.Value} {parameter.Unit}".Trim()));

            var followUp = HasExecutableMappings()
                ? "如果这些参数无误，请点击“确认并生成 SolidWorks 模型”。"
                : "当前阶段将先保留这些参数识别结果，不会凭空修改 SolidWorks 模型。";
            AddAssistantMessage($"我已识别到以下设计参数：{summary}。{followUp}");
            return;
        }

        if (MissingItems.Count > 0)
        {
            AddAssistantMessage($"我还需要你补充以下信息：{string.Join("。", MissingItems)}");
            return;
        }

        AddAssistantMessage("我还没有获得足够的设计参数，请继续描述模型类型、尺寸和目标。");
    }

    private void HandleLocalNextStepGuidance(string reason)
    {
        var message = BuildNextStepSuggestionMessage();
        AppendLog($"[NextStep] Local next-step guidance handled {reason}");
        AppendLog($"[NextStep] WorkflowStage={_workflowStage}");
        AppendLog($"[NextStep] HasUnsavedModelChanges={_hasUnsavedModelChanges}");
        AppendLog($"[NextStep] LastModifiedParameterSummary={FormatSessionLogValue(_lastModifiedParameterSummary)}");
        AppendLog($"[NextStep] GeneratedMessage={message.Replace(Environment.NewLine, " ", StringComparison.Ordinal)}");
        AddAssistantMessage(message);
    }

    private bool TryHandleDispatchedNextStepGuidance(LlmParseResult? parseResult)
    {
        if (parseResult?.Actions is null || parseResult.Actions.Count == 0)
        {
            return false;
        }

        if (!parseResult.Actions.Any(action => string.Equals(action, "next_step_guidance", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        AppendLog("[NextStep] next_step_guidance action intercepted after parse");
        HandleLocalNextStepGuidance("after parse");
        return true;
    }

    private void UpdateWorkflowStateFromDispatchResult(LlmParseResult? parseResult, CommandDispatchResult dispatchResult)
    {
        if (!dispatchResult.Handled)
        {
            return;
        }

        var actions = parseResult?.Actions ?? [];
        var isModificationAction = actions.Any(action => string.Equals(action, "update_solidworks_dimensions", StringComparison.OrdinalIgnoreCase));
        var isOpenModelAction = actions.Any(action => string.Equals(action, "open_working_model", StringComparison.OrdinalIgnoreCase));
        var isQueryEditableParametersAction = actions.Any(action => string.Equals(action, "query_editable_parameters", StringComparison.OrdinalIgnoreCase));
        var isSaveAction = actions.Any(action => string.Equals(action, "save_model", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "save", StringComparison.OrdinalIgnoreCase));
        var dispatchSucceeded = dispatchResult.Succeeded;

        AppendLog($"[Workflow] IsModificationAction={isModificationAction}");
        AppendLog($"[Workflow] DispatchSucceeded={dispatchSucceeded}");

        if (isOpenModelAction && dispatchSucceeded)
        {
            _workflowStage = AssistantWorkflowStage.ModelOpenedIdle;
            _hasUnsavedModelChanges = false;
            _hasQueriedEditableParameters = false;
            _lastSuccessfulActionSummary = "当前模型已经打开";
        }

        if (isQueryEditableParametersAction && dispatchSucceeded)
        {
            _hasQueriedEditableParameters = true;
        }

        if (isModificationAction && !dispatchSucceeded)
        {
            _workflowStage = AssistantWorkflowStage.ModificationFailed;
            AppendLog("[Workflow] ModificationSucceeded=False");
            AppendLog($"[Workflow] WorkflowStage={_workflowStage}");
            AppendLog($"[Workflow] HasUnsavedModelChanges={_hasUnsavedModelChanges}");
            return;
        }

        if (isModificationAction && dispatchSucceeded)
        {
            _lastModifiedParameterSummary = BuildModificationSummary(parseResult, dispatchResult);
            _lastSuccessfulActionSummary = _lastModifiedParameterSummary;
            _hasUnsavedModelChanges = false;
            _workflowStage = AssistantWorkflowStage.Saved;
            UpdateModificationProfile(parseResult, _lastModifiedParameterSummary);
            ClearPendingModificationContext("modification_succeeded");

            AppendLog("[Workflow] ModificationSucceeded=True");
            AppendLog($"[Workflow] LastModifiedParameterSummary={_lastModifiedParameterSummary}");
            AppendLog($"[Workflow] LastSuccessfulActionSummary={_lastSuccessfulActionSummary}");
            AppendLog($"[Workflow] WorkflowStage={_workflowStage}");
            AppendLog($"[Workflow] HasUnsavedModelChanges={_hasUnsavedModelChanges}");
            AppendLog($"[ModificationProfile] ModifiedDimensionKind={FormatModifiedDimensionKind(_lastModifiedDimensionKind)}");
            AppendLog("[Workflow] ClearPendingModificationAfterSuccess=True");
            return;
        }

        if (isSaveAction && dispatchSucceeded)
        {
            _workflowStage = AssistantWorkflowStage.Saved;
            _hasUnsavedModelChanges = false;
            _lastSuccessfulActionSummary = "当前模型已经保存";
            AppendLog("[Workflow] SaveSucceeded=True");
            AppendLog($"[Workflow] WorkflowStage={_workflowStage}");
            AppendLog($"[Workflow] HasUnsavedModelChanges={_hasUnsavedModelChanges}");
        }
    }

    private string BuildModificationSummary(LlmParseResult? parseResult, CommandDispatchResult dispatchResult)
    {
        var fromParameters = TryBuildModificationSummaryFromParameters(parseResult?.Parameters);
        if (!string.IsNullOrWhiteSpace(fromParameters))
        {
            AppendLog($"[Workflow] StructuredModificationSummary={fromParameters}");
            return fromParameters;
        }

        var mergedAssistantReply = MergeAssistantMessages(dispatchResult.AssistantMessages);
        var fromText = TryExtractModificationSummaryFromText(mergedAssistantReply);
        AppendLog($"[Workflow] TextFallbackUsed={!string.IsNullOrWhiteSpace(fromText)}");
        return string.IsNullOrWhiteSpace(fromText) ? "模型参数修改已完成" : fromText;
    }

    private static string TryBuildModificationSummaryFromParameters(IReadOnlyList<LlmParsedParameter>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return string.Empty;
        }

        decimal? upperWidth = null;
        decimal? upperHeight = null;
        decimal? upperThickness = null;
        decimal? lowerWidth = null;
        decimal? lowerHeight = null;
        decimal? lowerThickness = null;

        foreach (var parameter in parameters)
        {
            var normalizedName = NormalizeIntentText($"{parameter.Name}{parameter.DisplayName}");
            var value = TryGetDecimalValue(parameter.Value);
            if (value is null)
            {
                continue;
            }

            if (normalizedName.Contains("上弦杆截面宽度", StringComparison.OrdinalIgnoreCase))
            {
                upperWidth = value;
            }
            else if (normalizedName.Contains("上弦杆截面高度", StringComparison.OrdinalIgnoreCase))
            {
                upperHeight = value;
            }
            else if (normalizedName.Contains("上弦杆壁厚", StringComparison.OrdinalIgnoreCase))
            {
                upperThickness = value;
            }
            else if (normalizedName.Contains("下弦杆截面宽度", StringComparison.OrdinalIgnoreCase))
            {
                lowerWidth = value;
            }
            else if (normalizedName.Contains("下桁架截面宽度", StringComparison.OrdinalIgnoreCase))
            {
                lowerWidth = value;
            }
            else if (normalizedName.Contains("下弦杆截面高度", StringComparison.OrdinalIgnoreCase))
            {
                lowerHeight = value;
            }
            else if (normalizedName.Contains("下桁架截面高度", StringComparison.OrdinalIgnoreCase))
            {
                lowerHeight = value;
            }
            else if (normalizedName.Contains("下弦杆壁厚", StringComparison.OrdinalIgnoreCase))
            {
                lowerThickness = value;
            }
            else if (normalizedName.Contains("下桁架壁厚", StringComparison.OrdinalIgnoreCase))
            {
                lowerThickness = value;
            }
        }

        if (upperWidth.HasValue && upperHeight.HasValue)
        {
            return upperThickness.HasValue
                ? $"桁架上弦杆截面 {upperWidth:0.##}mm x {upperHeight:0.##}mm x {upperThickness:0.##}mm"
                : $"桁架上弦杆截面 {upperWidth:0.##}mm x {upperHeight:0.##}mm";
        }

        if (lowerWidth.HasValue && lowerHeight.HasValue)
        {
            return lowerThickness.HasValue
                ? $"桁架下弦杆截面 {lowerWidth:0.##}mm x {lowerHeight:0.##}mm x {lowerThickness:0.##}mm"
                : $"桁架下弦杆截面 {lowerWidth:0.##}mm x {lowerHeight:0.##}mm";
        }

        return string.Empty;
    }

    private string BuildClearModificationUserReply(
        LlmParseResult? parseResult,
        CommandDispatchResult dispatchResult,
        string fallbackReply)
    {
        if (dispatchResult.Succeeded)
        {
            return BuildClearModificationSuccessReply(parseResult);
        }

        var reason = string.IsNullOrWhiteSpace(fallbackReply)
            ? "执行失败，但当前没有返回更详细的错误信息。"
            : fallbackReply.Trim();

        return $"""
修改未完成。

原因：
{reason}

请确认模型已打开，且对应构件支持修改。
""".Trim();
    }

    private string BuildDimensionModificationUserReply(
        LlmParseResult? parseResult,
        CommandDispatchResult dispatchResult,
        string fallbackReply)
    {
        if (dispatchResult.Succeeded &&
            !string.IsNullOrWhiteSpace(fallbackReply) &&
            fallbackReply.Contains("模型路径：", StringComparison.Ordinal) &&
            fallbackReply.Contains("已修改尺寸：", StringComparison.Ordinal))
        {
            return fallbackReply.Trim();
        }

        var friendlyReply = BuildFriendlyParameterModificationSuccessReply(dispatchResult);
        if (!string.IsNullOrWhiteSpace(friendlyReply))
        {
            return friendlyReply;
        }

        return BuildClearModificationUserReply(parseResult, dispatchResult, fallbackReply);
    }

    private string BuildClearModificationSuccessReply(LlmParseResult? parseResult)
    {
        var summary = BuildShortSuccessfulModificationReply(parseResult);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        var details = BuildClearModificationDetails(parseResult);
        if (details.Count == 0)
        {
            return "已完成修改。";
        }

        var builder = new StringBuilder();
        builder.AppendLine("已完成修改。");
        builder.AppendLine();
        builder.AppendLine("修改内容：");
        foreach (var detail in details)
        {
            builder.AppendLine($"- {detail}");
        }

        builder.AppendLine();
        builder.Append("模型已更新并保存。");
        return builder.ToString().TrimEnd();
    }

    private string BuildFriendlyParameterModificationSuccessReply(CommandDispatchResult dispatchResult)
    {
        if (!dispatchResult.Succeeded || Parameters.Count == 0)
        {
            return string.Empty;
        }

        var displayLines = Parameters
            .Where(parameter => string.Equals(parameter.MappingStatus, "OK", StringComparison.OrdinalIgnoreCase))
            .Select(parameter => new
            {
                Name = ResolveParameterDisplayName(parameter).Trim(),
                Value = (parameter.Value ?? string.Empty).Trim(),
                Unit = string.IsNullOrWhiteSpace(parameter.Unit) ? "mm" : parameter.Unit.Trim()
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{item.Name}修改为 {item.Value} {item.Unit}".Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return displayLines.Count == 1
            ? $"已完成：{displayLines[0]}。"
            : string.Empty;
    }

    private string BuildShortSuccessfulModificationReply(LlmParseResult? parseResult)
    {
        var summary = TryBuildModificationSummaryFromParameters(parseResult?.Parameters);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        summary = summary.Replace("截面 ", string.Empty, StringComparison.Ordinal);
        return $"已完成：{summary}。";
    }

    private List<string> BuildClearModificationDetails(LlmParseResult? parseResult)
    {
        var details = new List<string>();
        if (parseResult?.Parameters is null || parseResult.Parameters.Count == 0)
        {
            return details;
        }

        decimal? upperWidth = null;
        decimal? upperHeight = null;
        decimal? upperThickness = null;
        decimal? lowerWidth = null;
        decimal? lowerHeight = null;
        decimal? lowerThickness = null;

        foreach (var parameter in parseResult.Parameters)
        {
            var normalizedName = NormalizeIntentText($"{parameter.Name}{parameter.DisplayName}");
            var value = TryGetDecimalValue(parameter.Value);
            if (value is null)
            {
                continue;
            }

            if (normalizedName.Contains("上弦杆截面宽度", StringComparison.OrdinalIgnoreCase))
            {
                upperWidth = value;
            }
            else if (normalizedName.Contains("上弦杆截面高度", StringComparison.OrdinalIgnoreCase))
            {
                upperHeight = value;
            }
            else if (normalizedName.Contains("上弦杆壁厚", StringComparison.OrdinalIgnoreCase))
            {
                upperThickness = value;
            }
            else if (normalizedName.Contains("下弦杆截面宽度", StringComparison.OrdinalIgnoreCase))
            {
                lowerWidth = value;
            }
            else if (normalizedName.Contains("下弦杆截面高度", StringComparison.OrdinalIgnoreCase))
            {
                lowerHeight = value;
            }
            else if (normalizedName.Contains("下弦杆壁厚", StringComparison.OrdinalIgnoreCase))
            {
                lowerThickness = value;
            }
        }

        AppendClearModificationDetails(details, "桁架上弦杆", upperWidth, upperHeight, upperThickness);
        AppendClearModificationDetails(details, "桁架下弦杆", lowerWidth, lowerHeight, lowerThickness);
        return details;
    }

    private string BuildOpenModelUserReply(CommandDispatchResult dispatchResult, string fallbackReply)
    {
        if (dispatchResult.Succeeded)
        {
            return """
模型已打开。

您现在可以修改桁架构件参数：
- 桁架上弦杆
- 桁架下弦杆

支持的修改方式：
- 单独修改截面
- 单独修改壁厚
- 同时修改截面和壁厚

示例：
把桁架上弦杆截面改成 80x80
把桁架上弦杆壁厚改成 6
把桁架上弦杆截面改成 80x80，壁厚改成 6
把下弦杆改成 80x80x6
""".Trim();
        }

        var reason = string.IsNullOrWhiteSpace(fallbackReply)
            ? "打开模型失败，但当前没有返回更详细的错误信息。"
            : fallbackReply.Trim();

        return $"""
模型未能打开。

原因：
{reason}

请确认 SolidWorks 可用，且模型文件路径存在。
""".Trim();
    }

    private static void AppendClearModificationDetails(
        ICollection<string> details,
        string memberName,
        decimal? width,
        decimal? height,
        decimal? thickness)
    {
        if (!width.HasValue && !height.HasValue && !thickness.HasValue)
        {
            return;
        }

        details.Add($"构件：{memberName}");

        if (width.HasValue && height.HasValue)
        {
            details.Add($"截面：{width:0.##}x{height:0.##}");
        }

        if (thickness.HasValue)
        {
            details.Add($"壁厚：{thickness:0.##}mm");
        }
    }

    private static decimal? TryGetDecimalValue(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind == JsonValueKind.Null || element.Value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var value = element.Value;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var numericValue) => numericValue,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var stringValue) => stringValue,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.CurrentCulture, out var localValue) => localValue,
            _ => null
        };
    }

    private static string TryExtractModificationSummaryFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var match = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*mm\s*[x×]\s*(\d+(?:\.\d+)?)\s*mm(?:\s*[x×]\s*(\d+(?:\.\d+)?)\s*mm)?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return string.Empty;
        }

        return match.Groups[3].Success
            ? $"桁架上弦杆截面 {match.Groups[1].Value}mm x {match.Groups[2].Value}mm x {match.Groups[3].Value}mm"
            : $"桁架上弦杆截面 {match.Groups[1].Value}mm x {match.Groups[2].Value}mm";
    }

    private void UpdateModificationProfile(LlmParseResult? parseResult, string summary)
    {
        var region = InferTrussRegion(parseResult, summary);
        var dimensionKind = InferModifiedDimensionKind(parseResult, summary);

        _lastModifiedRegion = region;
        _lastModifiedDimensionKind = dimensionKind;

        if (region is TrussRegion.UpperTruss or TrussRegion.Both)
        {
            _hasModifiedUpperTruss = true;
        }

        if (region is TrussRegion.LowerTruss or TrussRegion.Both)
        {
            _hasModifiedLowerTruss = true;
        }

        if (dimensionKind.HasFlag(ModifiedDimensionKind.SectionSize))
        {
            _hasModifiedSectionSize = true;
        }

        if (dimensionKind.HasFlag(ModifiedDimensionKind.WallThickness))
        {
            _hasModifiedWallThickness = true;
        }

        AppendLog($"[ModificationProfile] TrussRegion={region}");
        AppendLog($"[ModificationProfile] ModifiedDimensionKind={FormatModifiedDimensionKind(dimensionKind)}");
        AppendLog($"[ModificationProfile] HasModifiedUpperTruss={_hasModifiedUpperTruss}");
        AppendLog($"[ModificationProfile] HasModifiedLowerTruss={_hasModifiedLowerTruss}");
        AppendLog($"[ModificationProfile] HasModifiedSectionSize={_hasModifiedSectionSize}");
        AppendLog($"[ModificationProfile] HasModifiedWallThickness={_hasModifiedWallThickness}");
    }

    private static TrussRegion InferTrussRegion(LlmParseResult? parseResult, string summary)
    {
        var combinedText = BuildCombinedParameterText(parseResult) + summary;
        if ((combinedText.Contains("上桁架", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("上部桁架", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("上层桁架", StringComparison.OrdinalIgnoreCase)) &&
            (combinedText.Contains("下桁架", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("下部桁架", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("下层桁架", StringComparison.OrdinalIgnoreCase)))
        {
            return TrussRegion.Both;
        }

        if (combinedText.Contains("下桁架", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("下部桁架", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("下层桁架", StringComparison.OrdinalIgnoreCase))
        {
            return TrussRegion.LowerTruss;
        }

        if (combinedText.Contains("上桁架", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("上部桁架", StringComparison.OrdinalIgnoreCase) || combinedText.Contains("上层桁架", StringComparison.OrdinalIgnoreCase) || summary.Contains("上弦杆", StringComparison.OrdinalIgnoreCase))
        {
            return TrussRegion.UpperTruss;
        }

        if (summary.Contains("下弦杆", StringComparison.OrdinalIgnoreCase))
        {
            return TrussRegion.LowerTruss;
        }

        return TrussRegion.Unknown;
    }

    private static ModifiedDimensionKind InferModifiedDimensionKind(LlmParseResult? parseResult, string summary)
    {
        var combinedText = BuildCombinedParameterText(parseResult) + summary;
        var kind = ModifiedDimensionKind.None;

        if (combinedText.Contains("截面", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("宽", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("高", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(combinedText, @"\d+\s*[x×]\s*\d+", RegexOptions.IgnoreCase))
        {
            kind |= ModifiedDimensionKind.SectionSize;
        }

        if (combinedText.Contains("壁厚", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("厚度", StringComparison.OrdinalIgnoreCase) ||
            combinedText.Contains("wallthickness", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(combinedText, @"\d+\s*[x×]\s*\d+\s*[x×]\s*\d+", RegexOptions.IgnoreCase))
        {
            kind |= ModifiedDimensionKind.WallThickness;
        }

        return kind;
    }

    private static string BuildCombinedParameterText(LlmParseResult? parseResult)
    {
        if (parseResult?.Parameters is null)
        {
            return string.Empty;
        }

        return string.Join(" ", parseResult.Parameters.Select(parameter => $"{parameter.Name} {parameter.DisplayName}"));
    }

    private static string FormatModifiedDimensionKind(ModifiedDimensionKind kind)
    {
        if (kind == ModifiedDimensionKind.None)
        {
            return "None";
        }

        var values = new List<string>();
        if (kind.HasFlag(ModifiedDimensionKind.SectionSize))
        {
            values.Add(nameof(ModifiedDimensionKind.SectionSize));
        }

        if (kind.HasFlag(ModifiedDimensionKind.WallThickness))
        {
            values.Add(nameof(ModifiedDimensionKind.WallThickness));
        }

        return string.Join(", ", values);
    }

    private string BuildNextStepSuggestionMessage()
    {
        if (!string.IsNullOrWhiteSpace(_lastModifiedParameterSummary))
        {
            var savedState = _workflowStage == AssistantWorkflowStage.Saved || !_hasUnsavedModelChanges;
            var nextStepMessage = BuildAfterModificationNextStepMessage(savedState);
            AppendLog($"[NextStep] WorkflowStage={_workflowStage}");
            AppendLog($"[NextStep] HasUnsavedModelChanges={_hasUnsavedModelChanges}");
            AppendLog("[NextStep] AddedApdlSuggestion=True");
            return nextStepMessage;
        }

        string message = _workflowStage switch
        {
            AssistantWorkflowStage.ModelOpenedIdle => """
当前模型已经在 SolidWorks 中打开。

接下来您可以：
1. 查看模型整体结构；
2. 修改某个杆件尺寸；
3. 指定目标截面或壁厚让我帮您调整。

例如您可以说：“把桁架上弦杆截面改成 80x80”。
""".Trim(),
            AssistantWorkflowStage.ModificationSucceededUnsaved => BuildAfterModificationNextStepMessage(false),
            AssistantWorkflowStage.Saved => BuildAfterModificationNextStepMessage(true),
            AssistantWorkflowStage.ModelNotOpened or AssistantWorkflowStage.None => "当前还没有打开模型。\n\n您可以先说：“打开当前模型”。",
            AssistantWorkflowStage.ModificationFailed => "上一次模型修改没有成功。建议先检查日志和 SolidWorks 当前状态，再决定是否重新修改。",
            AssistantWorkflowStage.CleanupConfirmationPending => "当前模型可能仍被 SolidWorks 后台进程占用。\n\n您可以先确认是否清理后台占用，或者说“取消”暂不处理。",
            AssistantWorkflowStage.VisibleWindowCloseConfirmationPending => "我检测到 SolidWorks 窗口仍在打开。\n\n如果继续强制关闭，可能会丢失未保存修改。请您明确回复“确认关闭并继续”，或者说“取消”。",
            _ => "当前还没有打开模型。\n\n您可以先说：“打开当前模型”。"
        };

        AppendLog($"[NextStep] WorkflowStage={_workflowStage}");
        AppendLog($"[NextStep] HasUnsavedModelChanges={_hasUnsavedModelChanges}");
        AppendLog($"[NextStep] AddedApdlSuggestion=True");
        return message;
    }

    private string BuildAfterModificationNextStepMessage(bool isSaved)
    {
        var intro = isSaved
            ? $"刚才已经完成了：{_lastModifiedParameterSummary}，当前模型已保存。"
            : $"刚才已经完成了：{_lastModifiedParameterSummary}。";

        var suggestions = BuildContextAwareNextStepSuggestions(isSaved);
        var builder = new StringBuilder();
        builder.AppendLine(intro);
        builder.AppendLine();
        builder.AppendLine(isSaved ? "下一步可以继续：" : "下一步建议您按下面顺序继续：");

        for (var index = 0; index < suggestions.Count; index++)
        {
            builder.AppendLine($"{index + 1}. {suggestions[index]}");
        }

        var message = builder.ToString().Trim();
        AppendLog($"[NextStep] GeneratedSuggestions={string.Join(" | ", suggestions)}");
        return message;
    }

    private List<string> BuildContextAwareNextStepSuggestions(bool isSaved)
    {
        var suggestions = new List<string>();

        if (_lastModifiedRegion == TrussRegion.UpperTruss && !_hasModifiedLowerTruss)
        {
            suggestions.Add("如果需要上下结构匹配，可以继续检查或修改下桁架对应部分，保证上下结构参数一致。");
        }
        else if (_lastModifiedRegion == TrussRegion.LowerTruss && !_hasModifiedUpperTruss)
        {
            suggestions.Add("您刚才修改了下桁架部分，建议回头检查上桁架对应部分，确认上下结构参数是否需要保持一致。");
        }
        else if (isSaved && string.IsNullOrWhiteSpace(_lastModifiedParameterSummary))
        {
            suggestions.Add("检查上桁架和下桁架参数是否一致，确认截面尺寸、壁厚和关键结构参数是否满足设计要求。");
        }

        if (_lastModifiedDimensionKind == ModifiedDimensionKind.SectionSize)
        {
            suggestions.Add("本次主要修改了截面尺寸，壁厚暂未调整。建议您确认是否需要同步修改壁厚，例如补充指定 6mm、8mm 等壁厚。");
        }
        else if (_lastModifiedDimensionKind == ModifiedDimensionKind.WallThickness)
        {
            suggestions.Add("本次主要修改了壁厚，截面宽高暂未调整。建议您确认截面尺寸是否也需要同步优化，例如 80mm x 80mm。");
        }
        else if (_lastModifiedDimensionKind.HasFlag(ModifiedDimensionKind.SectionSize) && _lastModifiedDimensionKind.HasFlag(ModifiedDimensionKind.WallThickness))
        {
            suggestions.Add("本次已经同时修改了截面尺寸和壁厚，建议先检查 SolidWorks 中的重建结果和关联零件是否正常。");
        }

        if (!isSaved)
        {
            suggestions.Add("如果 SolidWorks 中的重建结果没有问题，建议保存当前模型，避免后续操作丢失修改。");
        }
        else if (string.IsNullOrWhiteSpace(_lastModifiedParameterSummary))
        {
            suggestions.Add("如果还需要优化结构，可以继续修改其他杆件参数。");
        }

        suggestions.Add(BuildApdlSimulationSuggestion());
        return suggestions;
    }

    private string BuildApdlSimulationSuggestion()
    {
        return _hasUnsavedModelChanges
            ? "建议先检查并保存当前模型。当前结构参数确认后，可以继续进行 ANSYS APDL 仿真分析，包括材料与边界条件设置、载荷施加、网格划分、求解以及结果查看。"
            : "当前结构参数确认后，可以继续进行 ANSYS APDL 仿真分析，包括材料与边界条件设置、载荷施加、网格划分、求解以及结果查看。";
    }

    private void AddUserMessage(string message)
    {
        _conversationTurn++;
        ChatMessages.Add(new ChatMessage
        {
            IsUser = true,
            RoleDisplay = "您",
            Text = message
        });
    }

    private void AddAssistantMessage(string message)
    {
        ChatMessages.Add(new ChatMessage
        {
            IsUser = false,
            RoleDisplay = "AI",
            Text = message
        });
    }

    private static string MergeAssistantMessages(IReadOnlyList<string> assistantMessages)
    {
        if (assistantMessages.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            assistantMessages
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => message.Trim())
                .Distinct(StringComparer.Ordinal));
    }

    private string BuildCurrentSessionStateLog(string currentInput)
    {
        var hasActiveDocument = _solidWorksService.TryGetActiveDocumentInfoWithoutStartingSolidWorks(out var activeDocumentPath);
        var activeDocumentName = string.IsNullOrWhiteSpace(activeDocumentPath)
            ? string.Empty
            : Path.GetFileName(activeDocumentPath);
        var isModelOpened = hasActiveDocument ||
                            (!string.IsNullOrWhiteSpace(_workspaceManager.CurrentAssemblyPath) &&
                             _workspaceManager.IsWorkspaceInitialized);

        return $"[Session] 当前状态：WorkspaceLoaded={_workspaceManager.IsWorkspaceInitialized}, IsModelOpened={isModelOpened}, ActiveDocumentName={FormatSessionLogValue(activeDocumentName)}, WorkingModelPath={FormatSessionLogValue(_workspaceManager.WorkingModelPath)}, CurrentAssemblyPath={FormatSessionLogValue(_workspaceManager.CurrentAssemblyPath)}, LastOpenedModelPath={FormatSessionLogValue(_workspaceManager.LastOpenedModelPath)}, LastOpenedDocument={FormatSessionLogValue(_workspaceManager.LastOpenedDocumentPath)}, CurrentInput={FormatSessionLogValue(currentInput)}, LastAction=n/a, LastActionSuccess=n/a";
    }

    private static string FormatSessionLogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "n/a" : value.Trim();
    }

    private void RefreshSummaryProperties()
    {
        OnPropertyChanged(nameof(OverallTargetSummary));
        OnPropertyChanged(nameof(MissingItemsSummary));
        OnPropertyChanged(nameof(GenerationHint));
        OnPropertyChanged(nameof(CurrentStatusText));
    }

    private void RefreshActionState()
    {
        OnPropertyChanged(nameof(PrimaryActionText));
        _primaryActionCommand.RaiseCanExecuteChanged();
        _reparseCommand.RaiseCanExecuteChanged();
        _generateApdlCommand.RaiseCanExecuteChanged();
        _openApdlOutputFileCommand.RaiseCanExecuteChanged();
        _saveParameterMappingsCommand.RaiseCanExecuteChanged();
        _clearLogsCommand.RaiseCanExecuteChanged();
        _exportLogsCommand.RaiseCanExecuteChanged();
        _copyCurrentLogsCommand.RaiseCanExecuteChanged();
        _openLogFileCommand.RaiseCanExecuteChanged();
        _showErrorsOnlyCommand.RaiseCanExecuteChanged();
    }

    private void RefreshScanState(SolidWorksDimensionScanResult? latestResult = null)
    {
        var result = latestResult ?? _dimensionScanCatalogService.LoadLatestResultOrEmpty();
        DimensionScanSummary = result.Items.Count == 0
            ? "当前可编辑参数将按固定配置执行，不向普通用户展示扫描结果。"
            : "当前可编辑参数已根据固定配置完成映射，可直接修改尺寸。";

        MappingGuideText = "当前可编辑参数已根据固定配置完成映射，可直接修改尺寸。";

        foreach (var item in EditableTrussMemberMappings)
        {
            item.ReloadOptions();
        }

        OnPropertyChanged(nameof(HasDimensionScanResult));
    }

    private void LoadMappingItems()
    {
        EditableTrussMemberMappings.Clear();
        foreach (var member in _editableTrussMemberCatalogService.GetEnabledMembers())
        {
            EditableTrussMemberMappings.Add(new EditableTrussMemberMappingItemViewModel(member, _dimensionScanCatalogService));
        }
    }

    private static ParameterItem MapToParameterItem(LlmParsedParameter parameter)
    {
        return new ParameterItem
        {
            Name = parameter.Name,
            DisplayName = parameter.DisplayName,
            Value = ConvertJsonValueToString(parameter.Value),
            Unit = parameter.Unit,
            Targets = parameter.Target,
            Confidence = parameter.Confidence,
            Description = string.Empty
        };
    }

    private static string ConvertJsonValueToString(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null || value.Value.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        var element = value.Value;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => element.GetRawText()
        };
    }

    private string ResolveParameterDisplayName(ParameterItem parameter)
    {
        return string.IsNullOrWhiteSpace(parameter.DisplayName) ? parameter.Name : parameter.DisplayName;
    }

    private bool HasExecutableMappings()
    {
        return Parameters.Any(parameter =>
            string.Equals(parameter.MappingStatus, "OK", StringComparison.OrdinalIgnoreCase) &&
            (parameter.SolidWorksTargets.Count > 0 || parameter.ApdlReplacements.Count > 0));
    }

    private static bool IsExplicitSolidWorksOpenIntent(string input)
    {
        var normalized = NormalizeIntentText(input);
        return normalized.Contains("打开solidworks", StringComparison.Ordinal) ||
               normalized.Contains("启动solidworks", StringComparison.Ordinal);
    }

    private static bool IsExplicitInitialModelOpenIntent(string input)
    {
        var normalized = NormalizeIntentText(input);
        if (!normalized.Contains("打开", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("初始模型", StringComparison.Ordinal) ||
               normalized.Contains("初始的模型", StringComparison.Ordinal) ||
               normalized.Contains("这个初始模型", StringComparison.Ordinal) ||
               normalized.Contains("初始检查车模型", StringComparison.Ordinal) ||
               normalized.Contains("初始solidworks模型", StringComparison.Ordinal) ||
               normalized.Contains("初始的总装配体", StringComparison.Ordinal) ||
               normalized.Contains("打开初始solidworks模型", StringComparison.Ordinal) ||
               normalized.Contains("打开不伸缩单层检查车初始模型", StringComparison.Ordinal);
    }

    private static bool IsExplicitWorkingModelOpenIntent(string input)
    {
        var normalized = NormalizeIntentText(input);
        if (!normalized.Contains("打开", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("新模型", StringComparison.Ordinal) ||
               normalized.Contains("工作模型", StringComparison.Ordinal) ||
               normalized.Contains("待修改的模型", StringComparison.Ordinal) ||
               normalized.Contains("要修改的模型", StringComparison.Ordinal) ||
               normalized.Contains("即将修改的模型", StringComparison.Ordinal) ||
               normalized.Contains("即将要修改的模型", StringComparison.Ordinal) ||
               normalized.Contains("修改前的模型", StringComparison.Ordinal) ||
               normalized.Contains("待修改模型", StringComparison.Ordinal) ||
               normalized.Contains("新总装配体", StringComparison.Ordinal);
    }

    private static string NormalizeIntentText(string input)
    {
        return input.Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static bool IsUpperChordGuideIntent(string input)
    {
        var normalized = NormalizeIntentText(input);
        return normalized.Contains("修改尺寸", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("改尺寸", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("怎么改尺寸", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("我要改尺寸", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnsupportedTrussParameterIntent(string input)
    {
        var normalized = NormalizeIntentText(input);
        return normalized.Contains("下弦杆", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("下弦", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("下桁架", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("上下弦杆", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("上、下弦杆", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("上下桁架", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("腹板", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("盖板", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("翼缘板", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAskingNextStep(string input)
    {
        var normalized = NormalizeIntentText(input);
        return normalized.Contains("接下来干什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("接下来要干什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("接下来可以干什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("接下来做什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("接下来要做什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("接下来可以做什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("我接下来要干什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("下一步", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("下一步呢", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("然后呢", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("后面干什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("后面做什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("现在该干什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("现在该做什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("继续干什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("继续做什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("接下来可以干嘛", StringComparison.OrdinalIgnoreCase);
    }

    private UserIntentKind ClassifyUserIntent(string input)
    {
        if (IsGreetingIntent(input))
        {
            return UserIntentKind.Greeting;
        }

        if (IsCapabilityQuestionIntent(input))
        {
            return UserIntentKind.CapabilityQuestion;
        }

        if (IsNextStepQueryIntentForClassification(input))
        {
            return UserIntentKind.NextStepQuery;
        }

        if (IsOpenModelCommandIntentForClassification(input))
        {
            return UserIntentKind.OpenModelCommand;
        }

        if (IsClearModificationCommandIntent(input))
        {
            return UserIntentKind.ClearModificationCommand;
        }

        if (IsAmbiguousModificationCommandIntent(input))
        {
            return UserIntentKind.AmbiguousModificationCommand;
        }

        return UserIntentKind.Unknown;
    }

    private bool ShouldHandleIntentLocally(UserIntentKind intent)
    {
        return intent is UserIntentKind.Greeting or UserIntentKind.CapabilityQuestion or UserIntentKind.NextStepQuery or UserIntentKind.AmbiguousModificationCommand or UserIntentKind.Unknown;
    }

    private string BuildLocalIntentReply(UserIntentKind intent, string input)
    {
        var isModelOpened = IsModelOpenForLocalReply();
        var hasQueriedEditableParameters = _hasQueriedEditableParameters || HasDimensionScanResult;
        return intent switch
        {
            UserIntentKind.Greeting => isModelOpened
                ? hasQueriedEditableParameters
                    ? """
你好，当前模型已打开，且可编辑项已经可以参考。

现在请直接选择要修改的构件和参数类型，例如上弦杆或下弦杆，并说明要调整宽度、高度、壁厚或截面规格。
""".Trim()
                    : """
你好，当前模型已打开。

下一步建议先输入“查询可编辑参数”，确认当前支持修改哪些构件和尺寸；确认后再告诉我要修改哪个构件以及目标规格。
""".Trim()
                : """
你好，第一次使用建议按这个顺序进行：
1. 先打开或确认当前 SolidWorks 模型；
2. 模型打开后，输入“查询可编辑参数”查看当前支持修改的构件；
3. 确认要修改的构件后，再告诉我要调整的尺寸或壁厚。
""".Trim(),
            UserIntentKind.CapabilityQuestion => """
我目前可以协助你做这些事：
1. 打开或检查当前 SolidWorks 模型状态；
2. 查询当前可编辑参数；
3. 修改已配置构件的截面尺寸或壁厚；
4. 提供 APDL / ANSYS 相关说明和分析准备建议。

如果你刚开始，请先打开模型；模型已打开则输入“查询可编辑参数”。
""".Trim(),
            UserIntentKind.NextStepQuery => isModelOpened
                ? hasQueriedEditableParameters
                    ? """
当前模型已打开，且可编辑项已经说明。

下一步请直接选择要修改的构件和参数类型，例如上弦杆或下弦杆，并说明要调整宽度、高度、壁厚或截面规格。
""".Trim()
                    : """
当前模型已打开。

下一步建议先输入“查询可编辑参数”，确认当前支持修改哪些构件和尺寸。

查完之后，再告诉我要修改哪个构件，以及目标尺寸或壁厚。
""".Trim()
                : """
当前还没有打开模型。

第一步请先打开模型或确认 SolidWorks 当前模型状态。

模型打开后，再输入“查询可编辑参数”，确认可修改内容，然后再进行尺寸修改。
""".Trim(),
            UserIntentKind.AmbiguousModificationCommand => BuildAmbiguousModificationReply(input),
            _ => isModelOpened
                ? """
我还没有理解您的意思。

请直接输入明确的修改命令，例如：
把桁架上弦杆截面改成 80x80，壁厚改成 6
""".Trim()
                : """
我还没有理解您的意思。

当前还没有打开模型。
请先输入：
开启前桁架检查车模型
""".Trim()
        };
    }

    private bool IsModelOpenForLocalReply()
    {
        return _solidWorksService.TryGetActiveDocumentInfoWithoutStartingSolidWorks(out _);
    }

    private static string BuildAmbiguousModificationReply(string input)
    {
        var normalized = NormalizeIntentText(input);
        var member = ResolveAmbiguousMemberLabel(normalized);
        var sectionSize = TryExtractSectionSize(normalized);
        var thickness = TryExtractThicknessValue(normalized);
        var hasSectionKeyword = ContainsIntentKeyword(normalized, "截面", "宽高", "尺寸");
        var hasThicknessKeyword = ContainsIntentKeyword(normalized, "壁厚", "厚度");
        var hasGenericTrussKeyword = normalized.Contains("桁架", StringComparison.OrdinalIgnoreCase) && member is null;
        var hasFuzzyAdjustment = ContainsIntentKeyword(normalized, "改一下", "调大一点", "改大一点", "加厚一点", "调一下");
        var singleNumericValue = ExtractSingleNumericValue(normalized);

        if (member is not null && !hasSectionKeyword && !hasThicknessKeyword && sectionSize is null && thickness is null)
        {
            return $"""
您想修改{member}，但还需要说明要修改截面、壁厚，还是同时修改截面和壁厚。

请直接输入类似：
把桁架{member}截面改成 80x80，壁厚改成 6
""".Trim();
        }

        if (hasSectionKeyword && hasFuzzyAdjustment && member is null && sectionSize is null)
        {
            return """
您想修改截面，但还需要说明构件和目标截面。

请直接输入类似：
把桁架上弦杆截面改成 80x80
""".Trim();
        }

        if (hasThicknessKeyword && hasFuzzyAdjustment && member is null && thickness is null)
        {
            return """
您想修改壁厚，但还需要说明构件和目标壁厚。

请直接输入类似：
把桁架上弦杆壁厚改成 6
""".Trim();
        }

        if (hasSectionKeyword && sectionSize is not null && member is null)
        {
            return $"""
您提供了目标截面 {sectionSize}，但还需要说明要修改哪个构件。

请直接输入类似：
把桁架上弦杆截面改成 80x80
""".Trim();
        }

        if (hasThicknessKeyword && thickness is not null && member is null)
        {
            return $"""
您提供了目标壁厚 {thickness}mm，但还需要说明要修改哪个构件。

请直接输入类似：
把桁架上弦杆壁厚改成 6
""".Trim();
        }

        if (member is not null &&
            !string.IsNullOrWhiteSpace(singleNumericValue) &&
            !hasSectionKeyword &&
            !hasThicknessKeyword &&
            sectionSize is null &&
            thickness is null)
        {
            return $"""
您提到了{member}和数值 {singleNumericValue}，但还需要说明参数类型。

如果要改截面，请输入：
把桁架{member}截面改成 80x80

如果要改壁厚，请输入：
把桁架{member}壁厚改成 6
""".Trim();
        }

        if (hasGenericTrussKeyword)
        {
            return """
您想修改桁架，但还需要说明具体构件、参数和目标值。

请直接输入类似：
把桁架上弦杆截面改成 80x80，壁厚改成 6
""".Trim();
        }

        if (hasSectionKeyword)
        {
            return """
您想修改截面，但还需要说明构件和目标截面。

请直接输入类似：
把桁架上弦杆截面改成 80x80
""".Trim();
        }

        if (hasThicknessKeyword)
        {
            return """
您想修改壁厚，但还需要说明构件和目标壁厚。

请直接输入类似：
把桁架上弦杆壁厚改成 6
""".Trim();
        }

        return """
您想修改模型，但信息还不完整。

请说明构件、参数和目标值，例如：
把桁架上弦杆截面改成 80x80，壁厚改成 6
""".Trim();
    }

    private bool IsClearModificationCommandIntent(string input)
    {
        return _trussMemberCommandParser.TryParse(input, out _);
    }

    private static string? ResolveAmbiguousMemberLabel(string normalized)
    {
        if (ContainsIntentKeyword(normalized, "桁架上弦杆", "上弦杆", "上弦"))
        {
            return "上弦杆";
        }

        if (ContainsIntentKeyword(normalized, "桁架下弦杆", "下弦杆", "下弦"))
        {
            return "下弦杆";
        }

        return null;
    }

    private static string? TryExtractSectionSize(string normalized)
    {
        var match = Regex.Match(normalized, @"(\d+(?:\.\d+)?[x×]\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Replace('×', 'x') : null;
    }

    private static string? TryExtractThicknessValue(string normalized)
    {
        var labeledMatch = Regex.Match(normalized, @"(?:壁厚|厚度)(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
        if (labeledMatch.Success)
        {
            return labeledMatch.Groups[1].Value;
        }

        return null;
    }

    private static string ExtractSingleNumericValue(string normalized)
    {
        var match = Regex.Match(normalized, @"(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static bool IsGreetingIntent(string input)
    {
        var normalized = NormalizeIntentText(input);
        return normalized is "你好" or "您好" or "hi" or "hello" or "在吗" or "哈喽";
    }

    private static bool IsCapabilityQuestionIntent(string input)
    {
        var normalized = NormalizeIntentText(input);
        return normalized.Contains("你只有这些功能吗", StringComparison.Ordinal) ||
               normalized.Contains("你还能做什么", StringComparison.Ordinal) ||
               normalized.Contains("你可以做什么", StringComparison.Ordinal) ||
               normalized.Contains("你会什么", StringComparison.Ordinal) ||
               normalized.Contains("你支持哪些功能", StringComparison.Ordinal) ||
               normalized.Contains("目前有哪些功能", StringComparison.Ordinal) ||
               normalized.Contains("除了这些还能干啥", StringComparison.Ordinal) ||
               normalized.Contains("这个软件能干什么", StringComparison.Ordinal) ||
               normalized.Contains("这个软件可以干什么", StringComparison.Ordinal) ||
               normalized.Contains("软件能干什么", StringComparison.Ordinal) ||
               normalized.Contains("你能做什么", StringComparison.Ordinal);
    }

    private static bool IsNextStepQueryIntentForClassification(string input)
    {
        var normalized = NormalizeIntentText(input);
        return normalized.Contains("我接下来可以做什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("怎么开始", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("如何开始", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("第一步做什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("第一步要做什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("下一步做什么", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("现在怎么办", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("接下来呢", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("然后呢", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("我该怎么操作", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("我要怎么操作", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("怎么操作", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("我现在该干什么", StringComparison.OrdinalIgnoreCase) ||
               IsAskingNextStep(input);
    }

    private void AppendResetIntentDiagnostics(string input)
    {
        var normalized = NormalizeIntentText(input);
        var containsQuestion = IsQuestionOnlyInput(input);
        var containsOpenKeyword = ContainsIntentKeyword(normalized, "打开", "加载", "开启", "打开当前模型");
        var containsResetKeyword = ContainsIntentKeyword(normalized, "重置", "恢复", "重新复制", "全新");
        var containsInitialModelKeyword = ContainsIntentKeyword(normalized, "初始模型", "未修改", "没有修改过", "最初", "原始");
        var intentHint = ResolveResetIntentHint(input);

        AppendLog($"[UserInput] Length={input.Length}");
        AppendLog($"[UserInput] ContainsQuestion={containsQuestion}");
        AppendLog($"[UserInput] ContainsOpenKeyword={containsOpenKeyword}");
        AppendLog($"[UserInput] ContainsResetKeyword={containsResetKeyword}");
        AppendLog($"[UserInput] ContainsInitialModelKeyword={containsInitialModelKeyword}");
        AppendLog($"[UserInput] IntentHint={intentHint}");
    }

    private static string ResolveResetIntentHint(string input)
    {
        if (IsResetCapabilityQuestion(input))
        {
            return "ResetCapabilityQuestion";
        }

        if (IsExplicitResetAndOpenRequest(input) || IsResetConfirmationCommand(input))
        {
            return "ExplicitResetAndOpenRequest";
        }

        if (IsPossibleResetWorkspaceRequest(input))
        {
            return "PossibleResetWorkspaceRequest";
        }

        if (IsExplicitWorkingModelOpenIntent(input))
        {
            return "ExplicitOpenCurrentModel";
        }

        return "OrdinaryInput";
    }

    private static bool IsResetCapabilityQuestion(string input)
    {
        var normalized = NormalizeIntentText(input);
        return IsQuestionOnlyInput(input) &&
               ContainsIntentKeyword(normalized, "重置模型", "恢复模型", "初始模型", "未修改", "没有修改过");
    }

    private static bool IsExplicitResetAndOpenRequest(string input)
    {
        var normalized = NormalizeIntentText(input);
        return normalized.Contains("重置模型并打开初始模型", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("恢复到未修改的初始模型并打开", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("确认重置并打开初始模型", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("重新复制初始模型并打开", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("打开一个全新的未修改模型", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("打开未修改过的初始模型", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResetConfirmationCommand(string input)
    {
        var normalized = NormalizeIntentText(input);
        return normalized.Contains("确认重置并打开初始模型", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPossibleResetWorkspaceRequest(string input)
    {
        var normalized = NormalizeIntentText(input);
        return (normalized.Contains("没有修改过", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("未修改过", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("未修改", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("初始模型", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("最初", StringComparison.OrdinalIgnoreCase)) &&
               !IsQuestionOnlyInput(input) &&
               !IsExplicitResetAndOpenRequest(input);
    }

    private static bool IsOpenModelCommandIntentForClassification(string input)
    {
        var normalized = NormalizeIntentText(input);
        if (IsExplicitResetAndOpenRequest(input) || IsPossibleResetWorkspaceRequest(input))
        {
            return false;
        }

        return IsExplicitOpenModelCommand(input, normalized) ||
               IsPoliteOpenModelExecutionRequest(input, normalized) ||
               IsExplicitWorkingModelOpenIntent(input) ||
               IsExplicitInitialModelOpenIntent(input);
    }

    private static bool IsAmbiguousModificationCommandIntent(string input)
    {
        var normalized = NormalizeIntentText(input);
        if (!HasModificationSignal(normalized))
        {
            return false;
        }

        var hasMemberKeyword = ContainsIntentKeyword(normalized,
            "桁架上弦杆", "桁架下弦杆", "上弦杆", "下弦杆", "上弦", "下弦", "桁架");
        var hasParameterKeyword = ContainsIntentKeyword(normalized,
            "截面", "宽高", "尺寸", "壁厚", "厚度");
        var hasDoubleDimension = Regex.IsMatch(normalized, @"\d+(\.\d+)?[x×]\d+(\.\d+)?", RegexOptions.CultureInvariant);
        var hasTripleDimension = Regex.IsMatch(normalized, @"\d+(\.\d+)?[x×]\d+(\.\d+)?[x×]\d+(\.\d+)?", RegexOptions.CultureInvariant);
        var hasThicknessValue = Regex.IsMatch(normalized, @"(壁厚|厚度)\d+(\.\d+)?", RegexOptions.CultureInvariant);
        var hasSingleNumericValue = Regex.IsMatch(normalized, @"\d+(\.\d+)?", RegexOptions.CultureInvariant);
        var hasFuzzyAdjustment = ContainsIntentKeyword(normalized,
            "改一下", "调大一点", "改大一点", "加厚一点", "调一下");

        if (hasFuzzyAdjustment && (hasMemberKeyword || hasParameterKeyword))
        {
            return true;
        }

        if ((hasDoubleDimension || hasTripleDimension || hasThicknessValue) &&
            !(hasMemberKeyword && hasParameterKeyword))
        {
            return true;
        }

        if (hasMemberKeyword && !hasDoubleDimension && !hasTripleDimension && !hasThicknessValue)
        {
            return true;
        }

        if (hasParameterKeyword && !hasMemberKeyword)
        {
            return true;
        }

        return hasMemberKeyword && hasSingleNumericValue && !hasDoubleDimension && !hasThicknessValue;
    }

    private static bool HasModificationSignal(string normalized)
    {
        return ContainsIntentKeyword(normalized,
            "修改", "改成", "改为", "改一下", "调大", "调小", "调整", "加厚", "变成", "改");
    }

    private static bool HasExplicitModificationCommand(string input, string normalized)
    {
        if (IsParameterSupplementConsultationOnly(input, normalized))
        {
            return false;
        }

        var hasSectionValue = TryExtractSectionValues(input, out _, out _, out _);
        var hasThicknessValue = TryExtractThicknessValue(input, out _);
        if (!hasSectionValue && !hasThicknessValue)
        {
            return false;
        }

        if (hasSectionValue)
        {
            return ContainsIntentKeyword(normalized, "改成", "改为", "修改为", "调整为", "变成", "把") &&
                   (ContainsIntentKeyword(normalized, "截面", "尺寸", "宽高") ||
                    TryResolvePendingTargetMember(normalized, out _, out _));
        }

        return ContainsIntentKeyword(normalized, "壁厚", "厚度") &&
               ContainsIntentKeyword(normalized, "改成", "改为", "修改", "调整", "把", "加厚", "变成");
    }

    private static bool ContainsExecutableValue(string input)
    {
        return TryExtractSectionValues(input, out _, out _, out _) ||
               TryExtractThicknessValue(input, out _);
    }

    private static bool IsParameterSupplementConsultationOnly(string input, string normalized)
    {
        if (!ContainsIntentKeyword(normalized, "壁厚", "厚度"))
        {
            return false;
        }

        var hasSupplementIntent = ContainsIntentKeyword(normalized,
            "补充参数", "补充壁厚", "还能改", "还可以", "还能", "还想修改", "还想改", "我只修改了", "只修改了");
        if (!hasSupplementIntent)
        {
            return false;
        }

        return !HasExplicitModificationCommandLikeText(normalized) || !ContainsExecutableValue(input);
    }

    private static bool HasExplicitModificationCommandLikeText(string normalized)
    {
        return ContainsIntentKeyword(normalized, "改成", "改为", "修改为", "调整为", "把", "变成");
    }

    private static bool ShouldBypassPendingModificationForClarification(
        string input,
        string normalized,
        bool hasExplicitModificationCommand)
    {
        if (hasExplicitModificationCommand)
        {
            return false;
        }

        return IsQuestionOnlyInput(input) || IsParameterSupplementConsultationOnly(input, normalized);
    }

    private static bool TryBuildParameterSupplementConsultationReply(string input, string normalized, out string reply)
    {
        reply = string.Empty;
        if (!IsParameterSupplementConsultationOnly(input, normalized))
        {
            return false;
        }

        reply = "可以继续补充壁厚，例如：把上弦杆壁厚改成6，或者直接说壁厚改成6。";
        return true;
    }

    private static bool ShouldBlockDispatchBecauseQuestion(string userInput, LlmParseResult parseResult)
    {
        if (!HasDispatchCommands(parseResult) || !IsQuestionOnlyInput(userInput))
        {
            return false;
        }

        var normalized = NormalizeIntentText(userInput);
        if (HasExplicitModificationCommand(userInput, normalized))
        {
            return false;
        }

        if (IsExplicitOpenModelCommand(userInput, normalized) ||
            IsPoliteOpenModelExecutionRequest(userInput, normalized))
        {
            return false;
        }

        return true;
    }

    private static bool IsExplicitOpenModelCommand(string input, string normalized)
    {
        if (!ContainsIntentKeyword(normalized, "打开模型", "打开当前模型", "帮我打开模型", "请打开当前模型", "请打开模型", "加载模型", "开启模型"))
        {
            return false;
        }

        if (IsOpenModelCapabilityQuestion(input, normalized) || IsModelStatusQuestion(input))
        {
            return false;
        }

        return !textEndsWithQuestionToken(input, normalized) || normalized.Contains("我看看是什么模型", StringComparison.Ordinal) || normalized.Contains("看看", StringComparison.Ordinal);
    }

    private static bool IsPoliteOpenModelExecutionRequest(string input, string normalized)
    {
        if (!ContainsIntentKeyword(normalized, "打开模型", "打开当前模型", "帮我打开模型", "加载模型", "开启模型"))
        {
            return false;
        }

        if (!ContainsIntentKeyword(normalized, "能帮我", "可以", "能不能", "帮我"))
        {
            return false;
        }

        return !IsOpenModelCapabilityQuestion(input, normalized) && !IsModelStatusQuestion(input);
    }

    private static bool IsOpenModelCapabilityQuestion(string input, string normalized)
    {
        if (!ContainsIntentKeyword(normalized, "打开模型", "打开当前模型", "加载模型", "开启模型"))
        {
            return false;
        }

        return textEndsWithQuestionToken(input, normalized) &&
               ContainsIntentKeyword(normalized, "你能", "你会", "你支持", "能不能", "可以吗");
    }

    private static bool textEndsWithQuestionToken(string input, string normalized)
    {
        return input.Contains('？', StringComparison.Ordinal) ||
               input.Contains('?', StringComparison.Ordinal) ||
               normalized.EndsWith("吗", StringComparison.Ordinal) ||
               normalized.EndsWith("么", StringComparison.Ordinal) ||
               normalized.EndsWith("吗？", StringComparison.Ordinal) ||
               normalized.EndsWith("么？", StringComparison.Ordinal);
    }

    private void AppendOpenModelIntentDiagnostics(string input, string normalized)
    {
        if (!ContainsIntentKeyword(normalized, "打开模型", "打开当前模型", "加载模型", "开启模型", "帮我打开模型"))
        {
            return;
        }

        if (IsOpenModelCapabilityQuestion(input, normalized))
        {
            AppendLog("[Intent] CapabilityQuestion");
            return;
        }

        if (IsModelStatusQuestion(input))
        {
            AppendLog("[Intent] QuestionOnly");
            return;
        }

        if (IsPoliteOpenModelExecutionRequest(input, normalized))
        {
            AppendLog("[Intent] PoliteExecutionRequest");
            return;
        }

        if (IsExplicitOpenModelCommand(input, normalized))
        {
            AppendLog("[Intent] ExplicitOpenCommand");
        }
    }

    private static bool ContainsIntentKeyword(string text, params string[] candidates)
    {
        return candidates.Any(candidate => text.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsQuestionOnlyInput(string text)
    {
        var normalized = NormalizeIntentText(text);
        return text.Contains('？', StringComparison.Ordinal) ||
               text.Contains('?', StringComparison.Ordinal) ||
               normalized.Contains("吗", StringComparison.Ordinal) ||
               normalized.Contains("么", StringComparison.Ordinal) ||
               normalized.Contains("是不是", StringComparison.Ordinal) ||
               normalized.Contains("是否", StringComparison.Ordinal) ||
               normalized.Contains("要不要", StringComparison.Ordinal) ||
               normalized.Contains("还是", StringComparison.Ordinal) ||
               normalized.Contains("可以吗", StringComparison.Ordinal) ||
               normalized.Contains("能不能", StringComparison.Ordinal) ||
               normalized.Contains("应该", StringComparison.Ordinal);
    }

    public void AppendSystemLog(string message)
    {
        AppendLog(message, source: "System");
    }

    private void AppendLog(string message, Exception? exception = null, string source = "MainViewModel")
    {
        if (!CheckAccessToUiThread())
        {
            ExecuteOnUiThread(() => AppendLog(message, exception, source));
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

        _logBuilder.AppendLine(entry.DisplayText);
        LogEntries.Add(entry);
        EnsureLogCategoryExists(entry.Category);
        EnsureLogActionExists(entry.Action);
        AppendLogToFile(entry);
        UpdateFilteredLogs();
        RefreshActionState();
    }

    private string SanitizeLogText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sanitized = text;
        sanitized = Regex.Replace(sanitized, @"ApiBaseUrl=.*", "ApiBaseUrl=<redacted>", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"FullRequestUrl=.*", "FullRequestUrl=<redacted>", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"Authorization=.*", "Authorization=<redacted>", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"ApiKey=.*", "ApiKey=<redacted>", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(WorkingModelPath|CurrentAssemblyPath|LastOpenedModelPath|LastOpenedDocument|SourceTemplatePath|CurrentInput|ActiveDocumentName)=([^,\r\n]+)", "$1=<redacted>", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"([A-Za-z]:\\[^,\r\n，；;]+\\)?([^\\,\r\n，；;]+\.(SLDASM|SLDPRT|json|csv))", "<redacted-path>\\$2", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(?<label>[：:]\s*)(?<path>[A-Za-z]:\\[^,\r\n，；;]+)", "${label}<redacted-path>", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(?<!<redacted-path>)(?<path>[A-Za-z]:\\[^,\r\n，；;]+)", "<redacted-path>", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(RawAssistantContentPreview|RawResponsePreview|PreParseText|ErrorResponsePreview)=.*", "$1=<redacted>", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"输入内容：.*", "[System] UserInput=<redacted>", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"UserInput=.*", "UserInput=<redacted>", RegexOptions.IgnoreCase);
        return sanitized;
    }

    private string BuildSanitizedLogTextForExportOrCopy()
    {
        if (LogEntries.Count > 0)
        {
            return string.Join(
                Environment.NewLine,
                LogEntries.Select(entry => SanitizeLogText(entry.DisplayText)));
        }

        return SanitizeLogText(Logs);
    }

    private bool TryBuildSafeCopyLogText(string sourceText, out string safeText)
    {
        safeText = SanitizeLogText(sourceText);
        if (ContainsSensitiveLogText(safeText))
        {
            safeText = SanitizeLogText(safeText);
        }

        return !ContainsSensitiveLogText(safeText);
    }

    private static bool ContainsSensitiveLogText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Regex.IsMatch(text, @"ApiBaseUrl=(?!<redacted>)", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"FullRequestUrl=(?!<redacted>)", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"Authorization\s*[:=]\s*(?!<redacted>|Configured)", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"Bearer\s+(?!\*\*\*)", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"\bapi[_-]?key\s*[:=]", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"\bsk-[A-Za-z0-9_-]+", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"[A-Za-z]:\\", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"Raw(ResponsePreview|AssistantContentPreview|PreParseText)\s*=\s*\{", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"CurrentInput=(?!<redacted>)", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"UserInput=(?!<redacted>)", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"https?://", RegexOptions.IgnoreCase);
    }

    private bool TryValidateClipboardReadback(out bool clipboardContentRedacted)
    {
        clipboardContentRedacted = false;

        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                return false;
            }

            var text = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
            clipboardContentRedacted = !ContainsSensitiveLogText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TrySetClipboardTextAsync(string text, int maxRetries = 5)
    {
        text ??= string.Empty;

        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
                return true;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x800401D0)
            {
                AppendLog($"[Clipboard] OpenClipboard failed, retry {i + 1}/{maxRetries}, HResult=0x{ex.HResult:X8}");
                await Task.Delay(80 * (i + 1));
            }
            catch (ExternalException ex)
            {
                AppendLog($"[Clipboard] Clipboard external exception, retry {i + 1}/{maxRetries}: {ex.Message}");
                await Task.Delay(80 * (i + 1));
            }
        }

        return false;
    }

    private static bool CheckAccessToUiThread()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess();
    }

    private static void ExecuteOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action, DispatcherPriority.Send);
    }

    private void EnsureLogCategoryExists(string category)
    {
        if (!LogCategories.Any(item => string.Equals(item, category, StringComparison.OrdinalIgnoreCase)))
        {
            LogCategories.Add(category);
        }
    }

    private void EnsureLogActionExists(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        if (!LogActions.Any(item => string.Equals(item, action, StringComparison.OrdinalIgnoreCase)))
        {
            LogActions.Add(action);
        }
    }

    private void UpdateFilteredLogs()
    {
        IEnumerable<LogEntryItem> entries = LogEntries;

        if (!string.Equals(SelectedLogLevel, LogLevelAll, StringComparison.OrdinalIgnoreCase))
        {
            if (SelectedLogLevel is "Info" or "Warning" or "Error")
            {
                entries = entries.Where(entry => string.Equals(entry.Level, SelectedLogLevel, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                entries = entries.Where(entry => string.Equals(entry.Category, SelectedLogLevel, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.Equals(SelectedLogCategory, LogCategoryAll, StringComparison.OrdinalIgnoreCase))
        {
            entries = entries.Where(entry => string.Equals(entry.Category, SelectedLogCategory, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(SelectedLogAction, LogActionAll, StringComparison.OrdinalIgnoreCase))
        {
            entries = entries.Where(entry => string.Equals(entry.Action, SelectedLogAction, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(LogSearchKeyword))
        {
            entries = entries.Where(entry =>
                entry.DisplayText.Contains(LogSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                entry.Message.Contains(LogSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                entry.Exception.Contains(LogSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                entry.Source.Contains(LogSearchKeyword, StringComparison.OrdinalIgnoreCase));
        }

        Logs = string.Join(Environment.NewLine, entries.Select(entry => entry.DisplayText));
    }

    private static string ResolveLogCategory(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "System";
        }

        if (message.StartsWith("[") && message.Contains(']'))
        {
            var endIndex = message.IndexOf(']');
            if (endIndex > 1)
            {
                return message[1..endIndex];
            }
        }

        if (ContainsAny(message, "solidworks", "sldasm", "sldprt"))
        {
            return "SolidWorks";
        }

        if (ContainsAny(message, "workspace"))
        {
            return "Workspace";
        }

        if (ContainsAny(message, "json", "deserialize", "single json value"))
        {
            return "JSON";
        }

        if (ContainsAny(message, "mapping", "editable-truss-members", "dimensionname", "partfilepath"))
        {
            return "Mapping";
        }

        if (ContainsAny(message, "scan_", "query_", "recommend_", "confirm_", "update_", "reset_", "open_working_model", "action"))
        {
            return "Action";
        }

        if (ContainsAny(message, "llm", "gpt", "openai", "api", "model"))
        {
            return "AI";
        }

        return "System";
    }

    private static string StripLogCategoryPrefix(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        if (message.StartsWith("[") && message.Contains(']'))
        {
            var endIndex = message.IndexOf(']');
            if (endIndex >= 0 && endIndex < message.Length - 1)
            {
                return message[(endIndex + 1)..].TrimStart();
            }
        }

        return message;
    }

    private static string ResolveLogLevel(string message, Exception? exception)
    {
        if (exception is not null)
        {
            return "Error";
        }

        if (message.Contains("COM 打开文档成功", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("已激活文档：", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Warnings=0", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ActivateErrors=0", StringComparison.OrdinalIgnoreCase))
        {
            return "Info";
        }

        if (Regex.IsMatch(message, @"\bwarning\b", RegexOptions.IgnoreCase))
        {
            return "Warning";
        }

        if (Regex.IsMatch(message, @"\berror\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(message, @"\bfailed\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(message, @"\binvalid\b", RegexOptions.IgnoreCase))
        {
            return "Error";
        }

        return "Info";
    }

    private static string ResolveLogAction(string message)
    {
        var match = Regex.Match(message, @"[a-z]+(?:_[a-z0-9]+)+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : string.Empty;
    }

    private static bool ContainsAny(string value, params string[] keywords)
    {
        return keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private void AppendLogToFile(LogEntryItem entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
            File.AppendAllText(_logFilePath, entry.DisplayText + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static bool IsJsonFormatFailure(Exception exception)
    {
        if (exception is JsonException)
        {
            return true;
        }

        return ContainsAny(
            exception.Message,
            "single json value",
            "json",
            "deserialize",
            "invalid after",
            "unexpected character");
    }

    private static string FormatJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string ResolveConfigValue(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}



