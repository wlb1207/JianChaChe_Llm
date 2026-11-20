using LlmWpfPrototype.Services;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services.Rag;

namespace LlmWpfPrototype.ViewModels;

public sealed partial class MainViewModel
{
    private bool _isLocalKnowledgeBusy;
    private CancellationTokenSource? _chatCancellation;
    private string _chatStatus = "工程问题自动结合相关资料；云端生成会发送问题及本轮选中的资料片段。";
    private string _lastChatInput = string.Empty;
    private string? _chatRetrievalTopic;
    private AsyncRelayCommand? _cancelChatCommand;
    private AsyncRelayCommand? _confirmBusinessCommand;
    public string ChatStatus { get => _chatStatus; private set => SetProperty(ref _chatStatus, value); }
    public bool IsChatBusy => _isLocalKnowledgeBusy;
    public bool CanConfirmBusiness => _pendingBusinessPlan is not null && !_isLocalKnowledgeBusy && !_isGeneratingModel
        && !_primaryActionCommand.IsExecuting && DesignStatus == StatusPendingConfirmation && Parameters.Any();
    public AsyncRelayCommand CancelChatCommand => _cancelChatCommand ??= new AsyncRelayCommand(() => {
        _chatCancellation?.Cancel();
        return Task.CompletedTask;
    }, () => _isLocalKnowledgeBusy);
    public AsyncRelayCommand ConfirmBusinessCommand => _confirmBusinessCommand ??= new AsyncRelayCommand(
        ConfirmPendingBusinessAsync, () => CanConfirmBusiness);

    // Only current user input can select the existing business workflow.
    private static bool IsLiveParameterQuery(string input)
    {
        var text = NormalizeIntentText(input);
        if (IsNegativeOrCancelModificationText(input) ||
            System.Text.RegularExpressions.Regex.IsMatch(text, @"如何|怎样|怎么|为什么|建议|假如|例如|能否|可以")) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"上弦杆|下弦杆|下桁架|可编辑参数")) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(text, @"^(请|帮我|麻烦)*(查询|读取|查看|显示|获取).*(参数|尺寸|截面|壁厚)") ||
            System.Text.RegularExpressions.Regex.IsMatch(text, @"(当前|现在).*(截面|壁厚|尺寸|参数).*(多少|多大|当前值)");
    }

    private bool IsExplicitBusinessInput(string input)
    {
        if (IsLiveParameterQuery(input)) return true;
        var normalized = NormalizeIntentText(input);
        if (IsNegativeOrCancelModificationText(input) || IsModelStatusQuestion(input)
            || IsModificationStatusQueryText(input)
            || System.Text.RegularExpressions.Regex.IsMatch(normalized, @"如何|怎样|怎么|为什么|教我")
            || IsQuestionOnlyInput(input)
            || normalized.Contains("建议") || normalized.Contains("解释") || normalized.Contains("假如")
            || normalized.Contains("例如") || normalized.Contains("是否")) return false;
        if ((_pendingModificationContext is not null || _pendingResetWorkspaceConfirmation)
            && (normalized is "确认" or "取消" or "是" or "否" || ContainsExecutableValue(input)
                || (_pendingModificationContext?.MissingSlot == "member"
                    && TryResolvePendingTargetMember(normalized, out _, out _)))) return true;
        if (normalized is "查询可编辑参数" or "查看可编辑参数") return true;
        if (!HasExplicitModificationCommand(input, normalized)
            && !System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^(请|帮我|麻烦|我要|我想)*(把|将|修改|调整|加厚|打开|加载|启动|开启|重置|恢复|复原|配置|执行|运行)")) return false;
        var intent = ClassifyUserIntent(input);
        return intent is UserIntentKind.OpenModelCommand or UserIntentKind.ClearModificationCommand
            or UserIntentKind.AmbiguousModificationCommand
            || HasExplicitModificationCommand(input, normalized)
            || IsExplicitManualTrussMappingIntent(input)
            || ((_pendingModificationContext is not null || _pendingResetWorkspaceConfirmation)
                && (normalized is "确认" or "取消" or "是" or "否" || ContainsExecutableValue(input)
                    || (_pendingModificationContext?.MissingSlot == "member"
                        && TryResolvePendingTargetMember(normalized, out _, out _))))
            || normalized is "查询可编辑参数" or "查看可编辑参数";
    }

    private async Task SendAutomaticChatMessageAsync()
    {
        if (_isLocalKnowledgeBusy || _isGeneratingModel) return;
        var question = UserInput.Trim();
        if (question.Length == 0 || question.Length > LocalKnowledgeChatService.MaxQuestionCharacters)
        { ChatStatus = "请输入 1 至 2000 字符的问题；未发送请求。"; return; }
        using var cancellation = new CancellationTokenSource();
        _chatCancellation = cancellation;
        _isLocalKnowledgeBusy = true;
        _lastChatInput = question;
        OnPropertyChanged(nameof(IsChatBusy));
        RefreshActionState();
        var history = _conversationHistory.Select(x => new ConversationMessage { Role = x.Role, Content = x.Content, KnowledgeProvenance = x.KnowledgeProvenance }).ToList();
        ChatMessages.Add(new ChatMessage { IsUser = true, RoleDisplay = "您", Text = question });
        UserInput = string.Empty;
        ChatStatus = "正在理解问题、按需检索并请求模型回答；问答不会执行工程操作。";
        try
        {
            var search = LocalKnowledgeSearchService.CreateForApplication();
            var service = new AutomaticKnowledgeChatService(search.Search, _llmService);
            var result = await service.AnswerAsync(question, history, cancellation.Token, _chatRetrievalTopic);
            cancellation.Token.ThrowIfCancellationRequested();
            _chatRetrievalTopic = AutomaticKnowledgeChatService.Plan(question, history, _chatRetrievalTopic).Topic ?? _chatRetrievalTopic;
            ChatMessages.Add(new ChatMessage {
                RoleDisplay = "AI", Text = result.Answer, IsLocalKnowledgeReply = true,
                LocalSources = result.Sources, LocalCitationStatus = result.CitationStatus
            });
            _conversationHistory.Add(new ConversationMessage { Role = "user", Content = question, KnowledgeProvenance = result.UserProvenance });
            _conversationHistory.Add(new ConversationMessage { Role = "assistant", Content = result.Answer, KnowledgeProvenance = result.AnswerProvenance });
            while (_conversationHistory.Count > MaxConversationHistoryMessages) _conversationHistory.RemoveAt(0);
            _llmRuntimeStatus = "在线";
            ChatStatus = result.Sources.Count == 0 ? "回答完成；未提供规范资料作为依据。"
                : "回答完成；可展开本次来源与完整原文核对，资料适用性未经确认。";
        }
        catch (KnowledgeDisclosureException ex)
        {
            _llmRuntimeStatus = "异常";
            ChatStatus = ex.Message;
            ChatMessages.Add(new ChatMessage { RoleDisplay = "系统", Text = ChatStatus });
        }
        catch (OperationCanceledException)
        {
            ChatStatus = "已取消等待；未自动重试。不能据此证明远端已停止处理或计费。";
            ChatMessages.Add(new ChatMessage { RoleDisplay = "系统", Text = ChatStatus });
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("本地资料检索失败", StringComparison.Ordinal)
            || ex.Message.StartsWith("当前为离线 Mock", StringComparison.Ordinal)
            || ex.Message.StartsWith("选中资料含", StringComparison.Ordinal))
        {
            _llmRuntimeStatus = "异常";
            ChatStatus = ex.Message;
            ChatMessages.Add(new ChatMessage { RoleDisplay = "系统", Text = ChatStatus });
        }
        catch
        {
            _llmRuntimeStatus = "异常";
            ChatStatus = "模型问答未完成，请检查资料或模型服务配置；未自动重试、未回退模板、未执行工程操作。";
            ChatMessages.Add(new ChatMessage { RoleDisplay = "系统", Text = ChatStatus });
        }
        finally
        {
            _chatCancellation = null;
            _isLocalKnowledgeBusy = false;
            OnPropertyChanged(nameof(IsChatBusy));
            OnPropertyChanged(nameof(LlmConfigSummary));
            OnPropertyChanged(nameof(GptStatusSummary));
            RefreshActionState();
        }
    }

    public void CancelPendingChat() => _chatCancellation?.Cancel();
}
