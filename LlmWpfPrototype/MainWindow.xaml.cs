using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Chat;
using LlmWpfPrototype.Services.Project;
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
    private GridLength _savedSidebarWidth = new(288);

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
        var projectStore = new DesignProjectStoreService(
            Path.Combine(AppPaths.DataDirectory, "projects"),
            appSettings.SolidWorksWorkspace);

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
            trussMemberCommandParser,
            appSettings.Simulation,
            projectStore: projectStore);

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        SizeChanged += MainWindow_SizeChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(MainViewModel.ShowWelcomeDashboard), StringComparison.Ordinal) &&
            _viewModel.ShowWelcomeDashboard)
        {
            RequestScrollChatToTop();
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(MainViewModel.IsProjectSidebarVisible), StringComparison.Ordinal))
        {
            return;
        }

        if (_viewModel.IsProjectSidebarVisible)
        {
            SidebarColumn.MinWidth = 236;
            SidebarColumn.MaxWidth = 400;
            SidebarColumn.Width = _savedSidebarWidth.Value >= 236 ? _savedSidebarWidth : new GridLength(288);
            SplitterColumn.Width = new GridLength(10);
        }
        else
        {
            if (SidebarColumn.ActualWidth >= 236)
            {
                _savedSidebarWidth = new GridLength(SidebarColumn.ActualWidth);
            }
            SidebarColumn.MinWidth = 0;
            SidebarColumn.MaxWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
        }
    }

    private void UserInputTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.V)
        {
            if (TryPasteImageFromClipboard())
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel?.PrimaryActionCommand.CanExecute(null) == true)
        {
            _viewModel.PrimaryActionCommand.Execute(null);
        }
    }

    private bool TryPasteImageFromClipboard()
    {
        if (_viewModel is null)
        {
            return false;
        }

        try
        {
            // Prefer native lossless PNG clipboard stream when modern snipping tools provide one.
            if (System.Windows.Clipboard.ContainsData("PNG") &&
                System.Windows.Clipboard.GetData("PNG") is Stream pngStream &&
                pngStream.Length > 0)
            {
                using (pngStream)
                {
                    if (_viewModel.TryAddClipboardImageStream(pngStream))
                    {
                        return true;
                    }
                }
            }

            if (System.Windows.Clipboard.ContainsImage())
            {
                var bitmap = System.Windows.Clipboard.GetImage();
                if (bitmap is not null)
                {
                    _viewModel.TryAddClipboardImage(bitmap);
                    return true;
                }
            }

            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var dropped = System.Windows.Clipboard.GetFileDropList();
                var imagePaths = dropped
                    .Cast<string?>()
                    .Where(ChatImageAttachmentService.IsSupportedImageFile)
                    .Cast<string>()
                    .ToArray();
                if (imagePaths.Length > 0)
                {
                    _viewModel.TryAddImageFiles(imagePaths);
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private void Composer_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files &&
            files.Any(ChatImageAttachmentService.IsSupportedImageFile))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Composer_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_viewModel is null ||
            !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var imageFiles = files.Where(ChatImageAttachmentService.IsSupportedImageFile).ToArray();
        if (imageFiles.Length == 0)
        {
            return;
        }

        e.Handled = true;
        _viewModel.TryAddImageFiles(imageFiles);
        UserInputTextBox.Focus();
    }

    private void ProjectMetaTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _viewModel?.SaveProjectMetaCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel?.CancelEditProjectMetaCommand.Execute(null);
        }
    }

    private void ClearConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.ClearConversationCommand.CanExecute(null))
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            this,
            "确定要清空当前这一条对话中的消息吗？\n\n（不会影响当前项目三维模型及其他历史对话。）",
            "确认清空消息",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _viewModel.ClearConversationCommand.Execute(null);
        }
    }

    private void DeleteProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null ||
            sender is not System.Windows.Controls.MenuItem { Tag: DesignProjectItemViewModel projectItem })
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            this,
            $"确定要删除项目“{projectItem.RawName}”及其全部历史对话吗？\n\n此操作不可恢复。",
            "确认删除项目",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _viewModel.DeleteProjectCommand.Execute(projectItem);
        }
    }

    private void DeleteConversationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null ||
            sender is not System.Windows.Controls.MenuItem { Tag: DesignConversationItemViewModel convItem })
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            this,
            $"确定要删除对话“{convItem.Title}”吗？",
            "确认删除对话",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _viewModel.DeleteConversationCommand.Execute(convItem);
        }
    }

    private async void CopyBubbleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.DataContext is not ChatMessage message ||
            string.IsNullOrWhiteSpace(message.Text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(message.Text);
            button.Content = "已复制 ✓";
            await Task.Delay(1400);
            button.Content = "复制";
        }
        catch
        {
        }
    }

    private void ProjectMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        DependencyObject? current = button;
        while (current is not null and not Border { ContextMenu: not null })
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is Border { ContextMenu: { } menu } cardBorder)
        {
            menu.PlacementTarget = cardBorder;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        EnableCollectionSynchronization();
        AttachChatCollection();
        UpdateAdvancedDebugTabHeight();
        RequestScrollChatToBottom(force: true);
        RequestScrollLogToBottom(force: true);
        _ = _viewModel?.ProbeLlmConnectionAsync(isStartupProbe: true);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel?.FlushActiveSessionOnExit();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }
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

    private void RequestScrollChatToTop()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (ChatScrollViewer.IsLoaded && _viewModel?.ShowWelcomeDashboard == true)
            {
                ChatScrollViewer.ScrollToTop();
            }
        }, DispatcherPriority.Render);
    }

    private void ScrollChatToBottom(bool force)
    {
        if (_viewModel?.ShowWelcomeDashboard == true)
        {
            if (ChatScrollViewer.IsLoaded)
            {
                ChatScrollViewer.ScrollToTop();
            }
            return;
        }

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

    private void LogTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            RequestScrollLogToBottom(force: false);
        }
    }

    private void AdvancedDebugPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && ReferenceEquals(AdvancedDebugTabControl.SelectedItem, LogOutputTabItem))
        {
            RequestScrollLogToBottom(force: false);
        }
    }

    private async void CopyJsonButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            _viewModel is null ||
            string.IsNullOrWhiteSpace(_viewModel.JsonOutput))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_viewModel.JsonOutput);
            button.Content = "已复制 ✓";
            await Task.Delay(1400);
            button.Content = "复制 JSON";
        }
        catch
        {
        }
    }

    private void AdvancedDebugTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == AdvancedDebugTabControl &&
            ReferenceEquals(AdvancedDebugTabControl.SelectedItem, LogOutputTabItem))
        {
            RequestScrollLogToBottom(force: false);
        }
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

            if (!LogTextBox.IsVisible)
            {
                return;
            }

            LogTextBox.CaretIndex = LogTextBox.Text.Length;
            LogTextBox.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateAdvancedDebugTabHeight()
    {
        if (!IsLoaded)
        {
            return;
        }

        var targetHeight = Math.Max(220d, ActualHeight * 0.30d);
        AdvancedDebugTabControl.Height = targetHeight;
    }
}
