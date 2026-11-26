using System.Globalization;
using System.Text.Json;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services.Design;

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
        var boundaryAdvisory = BuildPendingPlanDesignBoundaryAdvisory();
        var reply = string.IsNullOrWhiteSpace(boundaryAdvisory)
            ? $"待确认修改：{summary}。确认后将校验、写入、重建、保存并回读核对。请发送“确认修改”或“取消修改”。"
            : $"待确认修改：{summary}。\n{boundaryAdvisory}\n确认后将校验、写入、重建、保存并回读核对。请发送“确认修改”或“取消修改”。";
        AddAssistantMessage(reply);
        AppendConversationHistory(input, reply);
        AppendLog("[Confirmation] DimensionPlanQueued; Dispatched=False");
        RefreshSummaryProperties();
        RefreshActionState();
    }

    private string BuildPendingPlanDesignBoundaryAdvisory()
    {
        var evaluations = EvaluatePendingPlanBoundaries();
        return DesignBoundaryEvaluationService.FormatCompactPreConfirmationBlock(evaluations);
    }

    private List<DesignBoundaryEvaluationResult> EvaluatePendingPlanBoundaries()
    {
        var results = new List<DesignBoundaryEvaluationResult>();
        var currentUpper = ActiveProject?.UpperChord ?? MemberSectionSpecRecord.CreateBaseline("upper_chord", "上弦杆");
        var currentLower = ActiveProject?.LowerChord ?? MemberSectionSpecRecord.CreateBaseline("lower_chord", "下弦杆");

        var roleTargets = new Dictionary<DesignBoundaryMemberRole, (decimal? Width, decimal? Height, decimal? Thickness)>();

        if (!string.IsNullOrWhiteSpace(_pendingBusinessExecutionInput) &&
            _trussMemberCommandParser.TryParse(_pendingBusinessExecutionInput, out var parsed) &&
            parsed.TargetMemberIds.Count > 0)
        {
            foreach (var memberId in parsed.TargetMemberIds)
            {
                var role = MapMemberIdToBoundaryRole(memberId);
                if (role == DesignBoundaryMemberRole.Unspecified)
                {
                    continue;
                }

                roleTargets[role] = (parsed.Width, parsed.Height, parsed.Thickness);
            }
        }

        if (roleTargets.Count == 0 && Parameters.Count > 0)
        {
            foreach (var parameter in Parameters)
            {
                var nameKey = $"{parameter.Name} {parameter.DisplayName} {parameter.Description}".ToLowerInvariant();
                var role = nameKey.Contains("top", StringComparison.Ordinal) || nameKey.Contains("upper", StringComparison.Ordinal) || nameKey.Contains("上弦", StringComparison.Ordinal)
                    ? DesignBoundaryMemberRole.UpperChord
                    : nameKey.Contains("bottom", StringComparison.Ordinal) || nameKey.Contains("lower", StringComparison.Ordinal) || nameKey.Contains("下弦", StringComparison.Ordinal) || nameKey.Contains("下桁架", StringComparison.Ordinal)
                        ? DesignBoundaryMemberRole.LowerChord
                        : DesignBoundaryMemberRole.Unspecified;

                if (role == DesignBoundaryMemberRole.Unspecified)
                {
                    continue;
                }

                roleTargets.TryGetValue(role, out var current);
                if (TryParseDecimalParameterValue(parameter.Value, out var numericValue))
                {
                    if (nameKey.Contains("thickness", StringComparison.Ordinal) || nameKey.Contains("壁厚", StringComparison.Ordinal))
                    {
                        current.Thickness = numericValue;
                    }
                    else if (nameKey.Contains("height", StringComparison.Ordinal) || nameKey.Contains("外高", StringComparison.Ordinal) || nameKey.Contains("高度", StringComparison.Ordinal))
                    {
                        current.Height = numericValue;
                    }
                    else if (nameKey.Contains("width", StringComparison.Ordinal) || nameKey.Contains("外宽", StringComparison.Ordinal) || nameKey.Contains("宽度", StringComparison.Ordinal))
                    {
                        current.Width = numericValue;
                    }

                    roleTargets[role] = current;
                }
            }
        }

        var profile = DesignBoundaryProfileLoader.Load();
        foreach (var (role, target) in roleTargets)
        {
            var currentSpec = role == DesignBoundaryMemberRole.UpperChord ? currentUpper : currentLower;
            var targetW = target.Width ?? currentSpec.WidthMm;
            var targetH = target.Height ?? currentSpec.HeightMm;
            var targetT = target.Thickness ?? currentSpec.ThicknessMm;
            results.Add(DesignBoundaryEvaluationService.EvaluateMemberSection(
                targetW, targetH, targetT, role, currentSpec, profile));
        }

        return results;
    }

    private static DesignBoundaryMemberRole MapMemberIdToBoundaryRole(string memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
        {
            return DesignBoundaryMemberRole.Unspecified;
        }

        if (memberId.StartsWith("upper", StringComparison.OrdinalIgnoreCase))
        {
            return DesignBoundaryMemberRole.UpperChord;
        }

        if (memberId.StartsWith("lower", StringComparison.OrdinalIgnoreCase))
        {
            return DesignBoundaryMemberRole.LowerChord;
        }

        return DesignBoundaryMemberRole.Unspecified;
    }

    private static bool TryParseDecimalParameterValue(string? rawValue, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var cleaned = rawValue.Trim().Replace("mm", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
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
