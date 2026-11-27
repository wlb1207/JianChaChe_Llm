using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using LlmWpfPrototype.Services.Chat;

namespace LlmWpfPrototype.Models;

/// <summary>
/// A user-attached image stored under the application's local attachments folder
/// for multimodal vision recognition and conversation persistence.
/// </summary>
public sealed class ChatImageAttachment
{
    public string Id { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Portable path relative to <see cref="LlmWpfPrototype.Services.AppPaths.AttachmentsDirectory"/>
    /// (uses forward slashes so persisted JSON contains no machine-specific drive letters).
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    public string MimeType { get; set; } = "image/png";

    public int Width { get; set; }

    public int Height { get; set; }

    public long ByteSize { get; set; }

    [JsonIgnore]
    public string? ExplicitFullPath { get; set; }

    [JsonIgnore]
    public string ResolvedFullPath =>
        !string.IsNullOrWhiteSpace(ExplicitFullPath)
            ? ExplicitFullPath
            : (ChatImageAttachmentService.TryResolveSafeAttachmentPath(RelativePath) ?? string.Empty);

    [JsonIgnore]
    public string SizeDisplay
    {
        get
        {
            var sizeText = ByteSize >= 1024 * 1024
                ? $"{ByteSize / (1024d * 1024d):0.##} MB"
                : $"{Math.Max(1, (int)Math.Round(ByteSize / 1024d))} KB";
            return Width > 0 && Height > 0
                ? $"{Width}×{Height} · {sizeText}"
                : sizeText;
        }
    }

    [JsonIgnore]
    public string TooltipText => string.IsNullOrWhiteSpace(FileName)
        ? $"点击放大预览（{SizeDisplay}）"
        : $"{FileName}（{SizeDisplay}）· 点击放大预览";

    [JsonIgnore]
    public BitmapSource? ThumbnailBitmap =>
        ChatImageAttachmentService.TryLoadBitmap(
            !string.IsNullOrWhiteSpace(ExplicitFullPath) ? ExplicitFullPath : RelativePath,
            decodePixelWidth: 320);

    public ChatImageAttachment Clone() => new()
    {
        Id = Id,
        FileName = FileName,
        RelativePath = RelativePath,
        MimeType = MimeType,
        Width = Width,
        Height = Height,
        ByteSize = ByteSize,
        ExplicitFullPath = ExplicitFullPath
    };
}

/// <summary>
/// Wire payload for OpenAI-compatible multimodal image_url content items.
/// </summary>
public sealed record LlmImagePayload(
    string MimeType,
    string Base64Data,
    string DisplayName,
    int Width = 0,
    int Height = 0)
{
    public string DataUri => $"data:{MimeType};base64,{Base64Data}";
}
