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

    private string BuildPendingBusinessPlanSummary()
    {
        var summary = string.Join("；", Parameters.Select(p => $"{ResolveParameterDisplayName(p)} = {p.Value} {p.Unit}".Trim()));
        if (!string.IsNullOrWhiteSpace(_pendingBusinessExecutionInput) &&
            _trussMemberCommandParser.TryParse(_pendingBusinessExecutionInput, out var parsed) &&
            parsed.TargetMemberIds.Count > 0)
        {
            var memberNames = parsed.TargetMemberIds
                .Select(_editableTrussMemberCatalogService.FindEnabledMemberById)
                .Where(member => member is not null)
                .Select(member => string.IsNullOrWhiteSpace(member!.Name) ? member.Id : member.Name.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (memberNames.Length > 0 && !summary.Contains(memberNames[0], StringComparison.Ordinal))
            {
                return $"{string.Join("、", memberNames)}（{summary}）";
            }
        }

        return summary;
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
        var summary = BuildPendingBusinessPlanSummary();
        var reply = $"待确认修改：{summary}。确认后将校验、写入、重建、保存并回读核对。请发送“确认修改”或“取消修改”。";
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
            const string changedReply = "待确认参数已变化，请重新提交修改请求并核对；未执行修改。";
            AddAssistantMessage(changedReply);
            AppendConversationHistory("确认修改", changedReply);
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
            await HandleParsedResultAsync(plan, string.Empty, input, isConfirmedCadPlan: true);
        }
        catch (Exception ex)
        {
            DesignStatus = StatusFailed;
            StatusMessage = $"模型修改失败：{ex.Message}";
            AppendLog(StatusMessage, ex);
            AddAssistantMessage(StatusMessage);
            AppendConversationHistory("确认修改", StatusMessage);
        }
        finally
        {
            _isGeneratingModel = false;
            RefreshActionState();
        }
    }
}
