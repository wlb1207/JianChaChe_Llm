using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Presentation;
using LlmWpfPrototype.Services;
using LlmWpfPrototype.Services.Chat;

namespace LlmWpfPrototype.ViewModels;

public sealed partial class MainViewModel
{
    private const string DefaultZeroTypingImagePrompt =
        "用户上传了工程图片但未输入文字说明。请先判断图片内容类型（CAD/参数图纸、ANSYS仿真云图、软件报错弹窗、规范表格或现场照片），准确提取其中的关键尺寸、数值、报错文本或结构信息，并结合当前桥梁检查车模型与多规范边界给出清晰、专业的工程分析与下一步建议。";

    public ObservableCollection<ChatImageAttachment> PendingImageAttachments { get; } = [];

    public RelayCommand PickImagesCommand { get; private set; } = null!;

    public RelayCommand<ChatImageAttachment> RemovePendingImageCommand { get; private set; } = null!;

    public RelayCommand ClearPendingImagesCommand { get; private set; } = null!;

    public AsyncRelayCommand<string> SendImagePresetCommand { get; private set; } = null!;

    public RelayCommand<ChatImageAttachment> PreviewImageAttachmentCommand { get; private set; } = null!;

    public bool HasPendingImages => PendingImageAttachments.Count > 0;

    public bool CanAddMoreImages => PendingImageAttachments.Count < ChatImageAttachmentService.MaxImagesPerTurn;

    public string PendingImagesCountText =>
        $"已选 {PendingImageAttachments.Count}/{ChatImageAttachmentService.MaxImagesPerTurn} 张";

    public string ImageAttachButtonTooltip =>
        $"上传工程图片（单次最多 {ChatImageAttachmentService.MaxImagesPerTurn} 张；也支持 Ctrl+V 粘贴截图或直接拖入输入框）";

    private void InitializeImageInputCommands()
    {
        PendingImageAttachments.CollectionChanged += (_, _) => NotifyPendingImagesChanged();

        PickImagesCommand = new RelayCommand(
            PickImagesFromDialog,
            () => !IsCurrentConversationBusy && !IsCadExclusiveOperationRunning && CanAddMoreImages);

        RemovePendingImageCommand = new RelayCommand<ChatImageAttachment>(
            item =>
            {
                if (item is not null && PendingImageAttachments.Remove(item))
                {
                    StatusMessage = HasPendingImages
                        ? $"已移除图片，当前还剩 {PendingImageAttachments.Count} 张待发送。"
                        : "已清空待发送图片。";
                }
            });

        ClearPendingImagesCommand = new RelayCommand(
            () =>
            {
                ClearPendingImageAttachments();
                StatusMessage = "已清空待发送图片。";
            },
            () => HasPendingImages);

        SendImagePresetCommand = new AsyncRelayCommand<string>(
            SendImagePresetAsync,
            _ => HasPendingImages && !IsCurrentConversationBusy && !IsCadExclusiveOperationRunning);

        PreviewImageAttachmentCommand = new RelayCommand<ChatImageAttachment>(item =>
        {
            if (item is null)
            {
                return;
            }

            var path = item.ResolvedFullPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                StatusMessage = "无法预览该图片：本地附件文件不存在或已被移除。";
                return;
            }

            RagFormattedText.OpenImageZoomWindow(item.FileName, path);
        });
    }

    private void PickImagesFromDialog()
    {
        if (!CanAddMoreImages)
        {
            StatusMessage = $"单轮最多只能附带 {ChatImageAttachmentService.MaxImagesPerTurn} 张图片，请先移除不需要的图片。";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"选择要识别的工程图片（最多 {ChatImageAttachmentService.MaxImagesPerTurn} 张）",
            Filter = ChatImageAttachmentService.FileDialogFilter,
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
        {
            TryAddImageFiles(dialog.FileNames);
        }
    }

    public int TryAddImageFiles(IEnumerable<string> filePaths, string? attachmentsRootOverride = null)
    {
        var list = filePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
        if (list.Count == 0)
        {
            return 0;
        }

        var added = 0;
        var skippedUnsupported = 0;
        var hitLimit = false;
        var lastError = string.Empty;
        var scope = GetCurrentAttachmentScopeFolder();

        foreach (var path in list)
        {
            if (PendingImageAttachments.Count >= ChatImageAttachmentService.MaxImagesPerTurn)
            {
                hitLimit = true;
                break;
            }

            if (!ChatImageAttachmentService.IsSupportedImageFile(path))
            {
                skippedUnsupported++;
                continue;
            }

            try
            {
                var saved = ChatImageAttachmentService.SaveFromFile(path, scope, attachmentsRootOverride);
                PendingImageAttachments.Add(saved);
                added++;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                AppendLog($"[Vision] 添加图片失败（{Path.GetFileName(path)}）：{ex.Message}");
            }
        }

        if (added > 0)
        {
            var limitNote = hitLimit
                ? $"（单次最多 {ChatImageAttachmentService.MaxImagesPerTurn} 张，超出部分已忽略）"
                : string.Empty;
            StatusMessage = $"已添加 {added} 张图片（当前共 {PendingImageAttachments.Count}/{ChatImageAttachmentService.MaxImagesPerTurn} 张）{limitNote}，可直接点击下方快捷识图或按 Enter 发送。";
        }
        else if (hitLimit)
        {
            StatusMessage = $"单次最多附带 {ChatImageAttachmentService.MaxImagesPerTurn} 张图片，请先移除部分图片再添加。";
        }
        else if (!string.IsNullOrWhiteSpace(lastError))
        {
            StatusMessage = $"图片添加失败：{lastError}";
        }
        else if (skippedUnsupported > 0)
        {
            StatusMessage = "仅支持 PNG / JPG / BMP / WebP / GIF / TIFF 格式的图片文件。";
        }

        return added;
    }

    public bool TryAddClipboardImage(BitmapSource bitmap, string? attachmentsRootOverride = null)
    {
        if (bitmap is null)
        {
            return false;
        }

        if (PendingImageAttachments.Count >= ChatImageAttachmentService.MaxImagesPerTurn)
        {
            StatusMessage = $"单次最多附带 {ChatImageAttachmentService.MaxImagesPerTurn} 张图片，请先移除部分图片再粘贴。";
            return false;
        }

        try
        {
            var scope = GetCurrentAttachmentScopeFolder();
            var saved = ChatImageAttachmentService.SaveFromBitmapSource(bitmap, "剪贴板截图", scope, attachmentsRootOverride);
            PendingImageAttachments.Add(saved);
            StatusMessage = $"已粘贴截图（{saved.SizeDisplay}，共 {PendingImageAttachments.Count}/{ChatImageAttachmentService.MaxImagesPerTurn} 张），可直接点快捷识图或按 Enter 发送。";
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"[Vision] 粘贴剪贴板截图失败：{ex.Message}");
            StatusMessage = $"粘贴截图失败：{ex.Message}";
            return false;
        }
    }

    public bool TryAddClipboardImageStream(Stream imageStream, string? attachmentsRootOverride = null)
    {
        if (imageStream is null)
        {
            return false;
        }

        if (PendingImageAttachments.Count >= ChatImageAttachmentService.MaxImagesPerTurn)
        {
            StatusMessage = $"单次最多附带 {ChatImageAttachmentService.MaxImagesPerTurn} 张图片，请先移除部分图片再粘贴。";
            return false;
        }

        try
        {
            var scope = GetCurrentAttachmentScopeFolder();
            var saved = ChatImageAttachmentService.SaveFromEncodedStream(imageStream, "剪贴板截图", scope, attachmentsRootOverride);
            PendingImageAttachments.Add(saved);
            StatusMessage = $"已粘贴截图（{saved.SizeDisplay}，共 {PendingImageAttachments.Count}/{ChatImageAttachmentService.MaxImagesPerTurn} 张），可直接点快捷识图或按 Enter 发送。";
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"[Vision] 粘贴剪贴板 PNG 流失败：{ex.Message}");
            return false;
        }
    }

    public void ClearPendingImageAttachments()
    {
        if (PendingImageAttachments.Count > 0)
        {
            PendingImageAttachments.Clear();
        }
        else
        {
            NotifyPendingImagesChanged();
        }
    }

    private void NotifyPendingImagesChanged()
    {
        OnPropertyChanged(nameof(HasPendingImages));
        OnPropertyChanged(nameof(CanAddMoreImages));
        OnPropertyChanged(nameof(PendingImagesCountText));
        OnPropertyChanged(nameof(ImageAttachButtonTooltip));
        PickImagesCommand?.RaiseCanExecuteChanged();
        ClearPendingImagesCommand?.RaiseCanExecuteChanged();
        SendImagePresetCommand?.RaiseCanExecuteChanged();
        _primaryActionCommand?.RaiseCanExecuteChanged();
    }

    private string GetCurrentAttachmentScopeFolder()
    {
        if (!string.IsNullOrWhiteSpace(_activeConversation?.Id))
        {
            return _activeConversation.Id;
        }

        if (!string.IsNullOrWhiteSpace(ActiveProject?.Id))
        {
            return ActiveProject.Id;
        }

        return "general";
    }

    private async Task SendImagePresetAsync(string? presetKey)
    {
        if (!HasPendingImages || IsCurrentConversationBusy || IsCadExclusiveOperationRunning)
        {
            return;
        }

        var (displayTag, basePrompt) = ResolveImagePreset(presetKey);
        var extraUserNote = (UserInput ?? string.Empty).Trim();
        var displayText = string.IsNullOrWhiteSpace(extraUserNote)
            ? displayTag
            : $"{displayTag} · {extraUserNote}";
        var effectivePrompt = string.IsNullOrWhiteSpace(extraUserNote)
            ? basePrompt
            : $"{basePrompt}\n用户补充说明：{extraUserNote}";

        var allowStructuredEdit = HasExplicitImageEditDirective(extraUserNote);
        await SendUserMessageCoreAsync(
            overrideUserDisplayText: displayText,
            overrideLlmPrompt: effectivePrompt,
            forceVisionReplyOnly: !allowStructuredEdit);
    }

    private async Task HandleMultimodalVisionTurnAsync(
        string rawTypedInput,
        string displayText,
        string? overrideLlmPrompt,
        IReadOnlyList<ChatImageAttachment> attachedImages,
        bool forceVisionReplyOnly)
    {
        var effectivePrompt = !string.IsNullOrWhiteSpace(overrideLlmPrompt)
            ? overrideLlmPrompt.Trim()
            : (!string.IsNullOrWhiteSpace(rawTypedInput) ? rawTypedInput : DefaultZeroTypingImagePrompt);
        var historyUserText = string.IsNullOrWhiteSpace(displayText)
            ? $"[附带 {attachedImages.Count} 张工程图片] 自动识图分析"
            : $"[附带 {attachedImages.Count} 张工程图片] {displayText}";

        var imagePayloads = ChatImageAttachmentService.LoadPayloadsForLlm(attachedImages);
        if (imagePayloads.Count == 0)
        {
            const string missingMsg = "附带的图片文件已不可读取，请重新上传或粘贴截图后再试。";
            StatusMessage = missingMsg;
            AddAssistantMessage(missingMsg);
            PersistActiveConversationSnapshot();
            return;
        }

        var task = BeginConversationTask($"正在识别 {imagePayloads.Count} 张工程图片并结合本车模型分析...");
        try
        {
            _lastDispatchHandled = false;
            _lastDispatchSuppressDefaultReply = false;
            _lastDispatchSucceeded = false;
            _lastExecutableActionSucceeded = false;

            if (_pendingBusinessPlan is not null)
            {
                AppendLogForTask(task, "[Confirmation] PendingDimensionPlanPreservedDuringVisionTurn=True");
            }

            var visionReplyOnly = forceVisionReplyOnly || !HasExplicitImageEditDirective(rawTypedInput);
            var replyOnlyIntent = visionReplyOnly
                ? new ReplyOnlyIntentContext(
                    UserIntentKind.Unknown,
                    "MultimodalVisionAnalysis",
                    "图片识别服务暂时不可用，请检查网络连接后点击重试。")
                : null;

            StatusMessage = $"正在识别 {imagePayloads.Count} 张工程图片，请稍候...";
            AppendLogForTask(task, $"[Vision] MultimodalTurnStarted Images={imagePayloads.Count} ReplyOnly={visionReplyOnly}");

            var mainHistory = _memoryStore.GetMainChatHistory();
            var runtimeContextSummary = BuildRuntimeContextSnapshot(effectivePrompt).ToPromptBlock();
            var requestOptions = new LlmChatRequestOptions(
                ReplyOnlyMode: visionReplyOnly,
                IntentLabel: replyOnlyIntent?.IntentLabel,
                AllowApdlLauncherAction: false,
                RuntimeContextSummary: runtimeContextSummary,
                UserImages: imagePayloads);

            var json = await _llmService.ParseDesignRequirementAsync(
                effectivePrompt,
                mainHistory,
                requestOptions,
                task.Cancellation.Token);
            task.Cancellation.Token.ThrowIfCancellationRequested();
            SetLlmRuntimeStatus("在线", "ChatGPT 服务已连接，点击可重新检测");

            var parseResult = DeserializeParseResult(json);
            if (IsTaskActiveOnUi(task))
            {
                JsonOutput = BuildDebugJsonDisplay(json, requestOptions, parseResult);
            }

            if (parseResult is null)
            {
                throw new LlmResponseParseException("图片识别结果解析失败。", TruncateForLog(json));
            }

            AppendLogForTask(task, $"[Vision] MultimodalTurnSucceeded Images={imagePayloads.Count}");
            await HandleParsedResultAsync(parseResult, json, historyUserText, replyOnlyIntent, task: task);
        }
        catch (OperationCanceledException)
        {
            const string cancelReply = "已取消图片识别等待，当前操作已停止。";
            AppendLogForTask(task, "[Vision] 用户主动取消图片识别任务。");
            AddAssistantMessageForTask(task, cancelReply, roleDisplay: "系统");
            if (IsTaskActiveOnUi(task))
            {
                if (_pendingBusinessPlan is null)
                {
                    DesignStatus = StatusWaitingInput;
                }

                StatusMessage = cancelReply;
                PersistActiveConversationSnapshot();
                RefreshSummaryProperties();
            }
        }
        catch (LlmResponseParseException ex)
        {
            SetLlmRuntimeStatus("异常", "ChatGPT 返回格式解析失败，点击可重新检测");
            if (IsTaskActiveOnUi(task))
            {
                StatusMessage = "图片识别结果解析失败，请稍后重试。";
            }

            AppendLogForTask(task, $"[Vision] 识图结果解析失败：{ex.Message}");
            AddAssistantMessageForTask(task, "图片识别结果解析失败，请稍后重试或补充简短文字说明。");
            if (IsTaskActiveOnUi(task))
            {
                RefreshSummaryProperties();
            }
        }
        catch (Exception ex)
        {
            SetLlmRuntimeStatus("异常", $"ChatGPT 连接异常：{ex.Message}，点击立即重试");
            if (IsTaskActiveOnUi(task))
            {
                StatusMessage = "图片识别服务调用失败，请检查网络或稍后重试。";
            }

            AppendLogForTask(task, $"[Vision] 多模态识图调用失败：{ex.Message}", ex, "MainViewModel");
            AddAssistantMessageForTask(task, "图片识别服务调用失败，请检查网络连接后点击右上角状态栏重试。");
            if (IsTaskActiveOnUi(task))
            {
                RefreshSummaryProperties();
            }
        }
        finally
        {
            EndConversationTask(task);
        }
    }

    internal static (string DisplayLabel, string Prompt) ResolveImagePreset(string? presetKey)
    {
        return (presetKey?.Trim().ToLowerInvariant()) switch
        {
            "dimensions" => (
                "识别尺寸",
                "请仔细识别图片中的结构截面尺寸、壁厚、材料或参数表数据，逐条列出识别结果（外宽×外高×壁厚），并与本车当前上/下弦杆截面及已挂载规范边界逐项对照；若图中有明确的参数修改需求，请给出清晰的待核对参数清单并提示用户核对后再确认改模。"),
            "contour" => (
                "解读云图",
                "请解读图片中的 ANSYS / 有限元仿真云图，提取图例中的最大应力（MPa）、最大变形/挠度（mm）、单位制及高应力/大变形部位，并结合本车 Q355B 许用应力以及 JT/T 1430、GB/T 3811 的刚度与强度边界给出校核结论与结构改进建议。"),
            "error" => (
                "排查报错",
                "请识别图片中的软件报错信息、警告弹窗或异常状态（如 SolidWorks 或 ANSYS APDL），分析最可能的根因，并给出分步排查与解决方法。"),
            "standard" => (
                "规范对照",
                "请识别图片中的工程规范条款、表格、公式或节点构造图，解释其核心工程要求，并结合本车已挂载的 6 本国标/行标与当前结构参数给出对照核查结论。"),
            _ => (
                "智能识图",
                DefaultZeroTypingImagePrompt)
        };
    }

    internal static bool HasExplicitImageEditDirective(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = NormalizeIntentText(text);
        if (IsNegativeActionIntent(text) || IsQuestionOnlyInput(text))
        {
            return false;
        }

        return normalized.Contains("按图改", StringComparison.Ordinal) ||
               normalized.Contains("按图片改", StringComparison.Ordinal) ||
               normalized.Contains("照图改", StringComparison.Ordinal) ||
               normalized.Contains("按图修改", StringComparison.Ordinal) ||
               normalized.Contains("按图片修改", StringComparison.Ordinal) ||
               normalized.Contains("改成图", StringComparison.Ordinal) ||
               normalized.Contains("按这个改", StringComparison.Ordinal) ||
               normalized.Contains("帮我改模", StringComparison.Ordinal) ||
               normalized.Contains("执行改模", StringComparison.Ordinal) ||
               HasExplicitModificationCommand(text, normalized);
    }
}
