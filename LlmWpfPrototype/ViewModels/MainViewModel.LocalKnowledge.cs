using LlmWpfPrototype.Services;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services.Rag;
using System.Text.RegularExpressions;

namespace LlmWpfPrototype.ViewModels;

public sealed partial class MainViewModel
{
    private bool _isLocalKnowledgeBusy;
    private readonly List<ConversationMessage> _knowledgeConversationHistory = [];
    private CancellationTokenSource? _chatCancellation;
    private string _chatStatus = "工程问题自动结合相关资料；云端生成会发送问题及本轮选中的资料片段。";
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

    private bool ShouldUseKnowledgeChat(string input, string normalized, UserIntentKind intent,
        ReplyOnlyIntentContext? replyOnly, bool hasExplicitModificationCommand)
    {
        if (IsLiveParameterQuery(input) || IsExplicitManualTrussMappingIntent(input)
            || replyOnly is not null && replyOnly.IntentLabel != "PureQuestion")
            return false;

        if (replyOnly is null)
        {
            if (intent != UserIntentKind.Unknown || hasExplicitModificationCommand || Regex.IsMatch(normalized,
                @"^(?:请|帮我|麻烦|我要|我想|给我)*(?:把|将|打开|加载|启动|开启|关闭|修改|改|调整|加厚|保存|查询|读取|查看|扫描|配置|重置|恢复|还原|生成|导出|执行|运行|确认|取消)"))
                return false;
        }

        return AutomaticKnowledgeChatService.Plan(input, _knowledgeConversationHistory, _chatRetrievalTopic).NeedsEvidence;
    }

    private async Task SendAutomaticChatMessageAsync(string question)
    {
        if (_isLocalKnowledgeBusy || _isGeneratingModel) return;
        if (question.Length == 0 || question.Length > LocalKnowledgeChatService.MaxQuestionCharacters)
        {
            ChatStatus = "请输入 1 至 2000 字符的问题；未发送请求。";
            AddAssistantMessage(ChatStatus);
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _chatCancellation = cancellation;
        _isLocalKnowledgeBusy = true;
        OnPropertyChanged(nameof(IsChatBusy));
        RefreshActionState();
        var history = _knowledgeConversationHistory.ToArray();
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
            if (result.UserProvenance is not null && result.AnswerProvenance is not null)
            {
                _knowledgeConversationHistory.Add(new ConversationMessage { Role = "user", Content = question, KnowledgeProvenance = result.UserProvenance });
                _knowledgeConversationHistory.Add(new ConversationMessage { Role = "assistant", Content = result.Answer, KnowledgeProvenance = result.AnswerProvenance });
                while (_knowledgeConversationHistory.Count > MaxConversationHistoryMessages)
                    _knowledgeConversationHistory.RemoveAt(0);
            }
            if (result.CitedIds.Count > 0 && result.InvalidIds.Count == 0)
            {
                _llmRuntimeStatus = "在线";
                ChatStatus = "回答完成，可展开本轮引用的资料片段。";
            }
            else
            {
                ChatStatus = result.CitationStatus;
            }
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
        catch (LocalKnowledgeCallException ex)
        {
            _llmRuntimeStatus = "异常";
            ChatStatus = ex.Message;
            AddAssistantMessage(ChatStatus);
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
