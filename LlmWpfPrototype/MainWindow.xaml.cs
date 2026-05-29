using System.Collections.Specialized;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Threading;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.ViewModels;

namespace LlmWpfPrototype;

public partial class MainWindow : Window
{
    private INotifyCollectionChanged? _chatMessagesCollection;
    private MainViewModel? _viewModel;
    private readonly object _chatMessagesSyncRoot = new();
    private readonly object _logEntriesSyncRoot = new();
    private readonly object _mappingItemsSyncRoot = new();
    private bool _isChatNearBottom = true;
    private bool _isScrollRequestPending;

    public MainWindow()
    {
        InitializeComponent();

        var validation = StartupValidator.Validate();
        if (!validation.IsValid)
        {
            App.WriteStartupLog("启动自检失败", new InvalidOperationException(validation.Message));
        }
        else if (!string.Equals(validation.Message, "启动自检通过。", StringComparison.Ordinal))
        {
            App.WriteStartupLog("启动自检提示", new InvalidOperationException(validation.Message));
        }

        var appSettings = AppConfigLoader.LoadSettings();
        var trussChordSectionConfigService = new TrussChordSectionConfigService();
        var editableTrussMemberCatalogService = new EditableTrussMemberCatalogService();
        var dimensionScanCatalogService = new DimensionScanCatalogService();
        var trussParameterMappingRecommendationService = new TrussParameterMappingRecommendationService(dimensionScanCatalogService);
        var parameterDictionaryService = new ParameterDictionaryService(trussChordSectionConfigService);
        var apdlReplacementService = new ApdlReplacementService();
        var solidWorksService = new SolidWorksService(appSettings.SolidWorksWorkspace);
        var trussParameterSetupStateService = new TrussParameterSetupStateService(solidWorksService, dimensionScanCatalogService, editableTrussMemberCatalogService);
        var workspaceManager = new ModelWorkspaceManager(appSettings.SolidWorksWorkspace, solidWorksService);
        var trussMemberCommandParser = new TrussMemberCommandParser(editableTrussMemberCatalogService);
        var commandDispatcher = new CommandDispatcher(solidWorksService, workspaceManager, editableTrussMemberCatalogService, trussMemberCommandParser, dimensionScanCatalogService, trussParameterMappingRecommendationService, trussParameterSetupStateService);
        var llmService = new LlmService(appSettings.Llm, parameterDictionaryService);
        DataContext = new MainViewModel(
            llmService,
            parameterDictionaryService,
            apdlReplacementService,
            solidWorksService,
            commandDispatcher,
            workspaceManager,
            appSettings.Llm,
            trussChordSectionConfigService,
            editableTrussMemberCatalogService,
            dimensionScanCatalogService,
            trussParameterMappingRecommendationService,
            trussMemberCommandParser);

        _viewModel = DataContext as MainViewModel;
        workspaceManager.ResetSessionCacheOnStartup(message => _viewModel?.AppendSystemLog(message));

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        SizeChanged += MainWindow_SizeChanged;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        EnableCollectionSynchronization();
        AttachChatCollection();
        UpdateAdvancedDebugTabHeight();
        RequestScrollChatToBottom(force: true);
        RequestScrollLogToBottom(force: true);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        DetachChatCollection();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAdvancedDebugTabHeight();
    }

    private void AttachChatCollection()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (ReferenceEquals(_chatMessagesCollection, viewModel.ChatMessages))
        {
            return;
        }

        DetachChatCollection();
        _chatMessagesCollection = viewModel.ChatMessages;
        _chatMessagesCollection.CollectionChanged += ChatMessages_CollectionChanged;
    }

    private void DetachChatCollection()
    {
        if (_chatMessagesCollection is null)
        {
            return;
        }

        _chatMessagesCollection.CollectionChanged -= ChatMessages_CollectionChanged;
        _chatMessagesCollection = null;
    }

    private void EnableCollectionSynchronization()
    {
        if (_viewModel is null)
        {
            return;
        }

        BindingOperations.EnableCollectionSynchronization(_viewModel.ChatMessages, _chatMessagesSyncRoot);
        BindingOperations.EnableCollectionSynchronization(_viewModel.LogEntries, _logEntriesSyncRoot);
        BindingOperations.EnableCollectionSynchronization(_viewModel.EditableTrussMemberMappings, _mappingItemsSyncRoot);
    }

    private void ChatMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var force = e.Action == NotifyCollectionChangedAction.Reset;
        RequestScrollChatToBottom(force);
    }

    private void ChatScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _isChatNearBottom = ChatScrollViewer.ScrollableHeight <= 0 ||
                            ChatScrollViewer.VerticalOffset >= ChatScrollViewer.ScrollableHeight - 24;
    }

    private void RequestScrollChatToBottom(bool force)
    {
        if (_isScrollRequestPending && !force)
        {
            return;
        }

        _isScrollRequestPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _isScrollRequestPending = false;
            ScrollChatToBottom(force);
        }, DispatcherPriority.Render);
    }

    private void ScrollChatToBottom(bool force)
    {
        if (!force && !_isChatNearBottom)
        {
            return;
        }

        if (!ChatScrollViewer.IsLoaded)
        {
            RequestScrollChatToBottom(force: true);
            return;
        }

        ChatScrollViewer.ScrollToVerticalOffset(ChatScrollViewer.ScrollableHeight);
        _isChatNearBottom = true;
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RequestScrollLogToBottom(force: false);
    }

    private void ScrollLogToBottomButton_Click(object sender, RoutedEventArgs e)
    {
        RequestScrollLogToBottom(force: true);
        LogTextBox.Focus();
    }

    private void LogAutoScrollCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        RequestScrollLogToBottom(force: true);
    }

    private void RequestScrollLogToBottom(bool force)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!force && (_viewModel?.IsLogAutoScrollEnabled != true))
            {
                return;
            }

            LogTextBox.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void UpdateAdvancedDebugTabHeight()
    {
        if (!IsLoaded)
        {
            return;
        }

        var targetHeight = Math.Max(260d, ActualHeight * 0.32d);
        AdvancedDebugTabControl.Height = targetHeight;
    }
}
