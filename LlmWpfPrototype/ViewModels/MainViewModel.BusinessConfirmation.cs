using System.Text.Json;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.ViewModels;

public sealed partial class MainViewModel
{
    private LlmParseResult? _pendingBusinessPlan;
    private string _pendingBusinessInput = string.Empty;
    private string _pendingBusinessExecutionInput = string.Empty;
    private string _pendingBusinessParameters = string.Empty;

    private void ClearPendingBusinessPlan()
    {
        if (_pendingBusinessPlan is null) return;
        _pendingBusinessPlan = null;
        _pendingBusinessInput = string.Empty;
        _pendingBusinessExecutionInput = string.Empty;
        _pendingBusinessParameters = string.Empty;
        Parameters.Clear();
        MissingItems.Clear();
        DesignStatus = StatusWaitingInput;
        RefreshActionState();
    }

    private void QueueBusinessPlanForConfirmation(LlmParseResult plan, string input)
    {
        _pendingBusinessPlan = JsonSerializer.Deserialize<LlmParseResult>(JsonSerializer.Serialize(plan))!;
        _pendingBusinessInput = input;
        // Pending-slot completion has a locally synthesized member+size command.
        // Keep it separate from the user's actual utterance used in conversation history.
        _pendingBusinessExecutionInput = _lastSubmittedInput;
        _pendingBusinessParameters = JsonSerializer.Serialize(Parameters);
        DesignStatus = StatusPendingConfirmation;
        StatusMessage = "尺寸修改尚未执行，等待确认。";
        var summary = string.Join("；", Parameters.Select(p => $"{ResolveParameterDisplayName(p)} = {p.Value} {p.Unit}"));
        var reply = $"待确认修改：{summary}。确认后按原流程校验、写入、重建、保存并回读核对；任一步失败均不报告成功。请点击“确认待执行业务”。";
        AddAssistantMessage(reply);
        AppendConversationHistory(input, reply);
        AppendLog("[Confirmation] DimensionPlanQueued; Dispatched=False");
        RefreshSummaryProperties();
        RefreshActionState();
    }

    private Task ConfirmPendingBusinessAsync() =>
        CanConfirmBusiness ? ExecuteConfirmedBusinessPlanAsync() : Task.CompletedTask;

    private async Task ExecuteConfirmedBusinessPlanAsync()
    {
        var plan = _pendingBusinessPlan!;
        var input = _pendingBusinessInput;
        var executionInput = _pendingBusinessExecutionInput;
        if (_pendingBusinessParameters != JsonSerializer.Serialize(Parameters))
        {
            ClearPendingBusinessPlan();
            AddAssistantMessage("待确认参数已变化，请重新提交修改请求并核对；未执行修改。");
            return;
        }
        _pendingBusinessPlan = null;
        _pendingBusinessInput = string.Empty;
        _pendingBusinessExecutionInput = string.Empty;
        _pendingBusinessParameters = string.Empty;
        plan.NeedConfirmation = false;
        _isGeneratingModel = true;
        _lastSubmittedInput = executionInput;
        RefreshActionState();
        try
        {
            AppendLog("[Confirmation] DimensionPlanConfirmed; Dispatching=True");
            await HandleParsedResultAsync(plan, string.Empty, input);
        }
        catch (Exception ex)
        {
            DesignStatus = StatusFailed;
            StatusMessage = $"模型修改失败：{ex.Message}";
            AppendLog(StatusMessage, ex);
            AddAssistantMessage(StatusMessage);
        }
        finally
        {
            _isGeneratingModel = false;
            RefreshActionState();
        }
    }
}
