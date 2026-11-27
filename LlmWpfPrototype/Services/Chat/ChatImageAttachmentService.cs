using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LlmWpfPrototype.Models;

namespace LlmWpfPrototype.Services.Chat;

/// <summary>
/// Handles validation, lossless/adaptive compression, disk persistence, safe path resolution,
/// and Base64 payload loading for user-attached engineering images.
/// </summary>
public static class ChatImageAttachmentService
{
    public const int MaxImagesPerTurn = 4;
    public const int MaxLongEdgePixels = 1600;
    public const long MaxInputFileBytes = 20L * 1024 * 1024;
    private const int LosslessPngThresholdBytes = 420_000;
    private const int JpegQualityLevel = 88;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".tif", ".tiff"
    };

    private static readonly Regex SafeScopeSegmentRegex = new(@"[^A-Za-z0-9_-]", RegexOptions.Compiled);

    public static string FileDialogFilter =>
        "工程图片 (*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.tif;*.tiff|所有文件 (*.*)|*.*";

    public static bool IsSupportedImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(ext) && SupportedExtensions.Contains(ext);
    }

    public static ChatImageAttachment SaveFromFile(
        string sourceFilePath,
        string? scopeFolder = null,
        string? attachmentsRoot = null)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("找不到指定的图片文件。", sourceFilePath);
        }

        if (!IsSupportedImageFile(sourceFilePath))
        {
            throw new InvalidOperationException($"不支持的图片格式：{Path.GetExtension(sourceFilePath)}（支持 PNG / JPG / BMP / WebP / GIF / TIFF）。");
        }

        var fileInfo = new FileInfo(sourceFilePath);
        if (fileInfo.Length <= 0)
        {
            throw new InvalidOperationException("图片文件为空，无法读取。");
        }

        if (fileInfo.Length > MaxInputFileBytes)
        {
            throw new InvalidOperationException($"图片文件过大（超过 {MaxInputFileBytes / (1024 * 1024)} MB），请压缩后再上传。");
        }

        var rawBytes = File.ReadAllBytes(sourceFilePath);
        using var stream = new MemoryStream(rawBytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidOperationException("无法解码图片内容，请确认文件未损坏。");
        }

        var frame = decoder.Frames[0];
        if (frame.CanFreeze)
        {
            frame.Freeze();
        }

        var displayName = Path.GetFileName(sourceFilePath);
        return SaveBitmapCore(frame, displayName, scopeFolder, attachmentsRoot);
    }

    public static ChatImageAttachment SaveFromBitmapSource(
        BitmapSource bitmap,
        string displayNamePrefix = "截图",
        string? scopeFolder = null,
        string? attachmentsRoot = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var timestampName = $"{displayNamePrefix}_{DateTime.Now:HHmmss}.png";
        var normalized = EnsureOpaqueBgr24(bitmap, forceStripAllZeroAlpha: true);
        return SaveBitmapCore(normalized, timestampName, scopeFolder, attachmentsRoot);
    }

    public static ChatImageAttachment SaveFromEncodedStream(
        Stream imageStream,
        string displayNamePrefix = "截图",
        string? scopeFolder = null,
        string? attachmentsRoot = null)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        if (imageStream.CanSeek)
        {
            imageStream.Position = 0;
        }

        using var ms = new MemoryStream();
        imageStream.CopyTo(ms);
        ms.Position = 0;

        var decoder = BitmapDecoder.Create(
            ms,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidOperationException("无法解码剪贴板图片流。");
        }

        var frame = decoder.Frames[0];
        if (frame.CanFreeze)
        {
            frame.Freeze();
        }

        var timestampName = $"{displayNamePrefix}_{DateTime.Now:HHmmss}.png";
        var normalized = EnsureOpaqueBgr24(frame, forceStripAllZeroAlpha: true);
        return SaveBitmapCore(normalized, timestampName, scopeFolder, attachmentsRoot);
    }

    public static IReadOnlyList<LlmImagePayload> LoadPayloadsForLlm(
        IEnumerable<ChatImageAttachment>? attachments,
        string? attachmentsRoot = null)
    {
        if (attachments is null)
        {
            return [];
        }

        var payloads = new List<LlmImagePayload>();
        foreach (var item in attachments)
        {
            if (item is null)
            {
                continue;
            }

            var path = !string.IsNullOrWhiteSpace(item.ExplicitFullPath) && File.Exists(item.ExplicitFullPath)
                ? item.ExplicitFullPath
                : TryResolveSafeAttachmentPath(item.RelativePath, attachmentsRoot);

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                continue;
            }

            var mime = string.IsNullOrWhiteSpace(item.MimeType)
                ? InferMimeTypeFromPath(path)
                : item.MimeType;

            payloads.Add(new LlmImagePayload(
                MimeType: mime,
                Base64Data: Convert.ToBase64String(bytes),
                DisplayName: string.IsNullOrWhiteSpace(item.FileName) ? Path.GetFileName(path) : item.FileName,
                Width: item.Width,
                Height: item.Height));
        }

        return payloads;
    }

    public static string? TryResolveSafeAttachmentPath(string? relativeOrAbsolutePath, string? attachmentsRoot = null)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
        {
            return null;
        }

        var trimmed = relativeOrAbsolutePath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return File.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var root = string.IsNullOrWhiteSpace(attachmentsRoot)
            ? AppPaths.AttachmentsDirectory
            : Path.GetFullPath(attachmentsRoot);
        var fullRoot = Path.GetFullPath(root);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        var normalizedRel = trimmed.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalizedRel));
        if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(candidate) ? candidate : null;
    }

    public static BitmapSource? TryLoadBitmap(
        string? relativeOrAbsolutePath,
        int decodePixelWidth = 0,
        string? attachmentsRoot = null)
    {
        try
        {
            var fullPath = TryResolveSafeAttachmentPath(relativeOrAbsolutePath, attachmentsRoot);
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(fullPath);
            using var ms = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
            if (decodePixelWidth > 0)
            {
                bitmap.DecodePixelWidth = decodePixelWidth;
            }

            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return RepairZeroAlphaIfNeeded(bitmap);
        }
        catch
        {
            return null;
        }
    }

    private static ChatImageAttachment SaveBitmapCore(
        BitmapSource source,
        string displayName,
        string? scopeFolder,
        string? attachmentsRoot)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            throw new InvalidOperationException("图片尺寸无效，无法处理。");
        }

        var (encodedBytes, mimeType, extension, finalWidth, finalHeight) = ResizeAndEncode(source);

        var root = string.IsNullOrWhiteSpace(attachmentsRoot)
            ? AppPaths.AttachmentsDirectory
            : Path.GetFullPath(attachmentsRoot);
        var safeScope = SanitizeScopeFolder(scopeFolder);
        var targetDir = Path.Combine(root, safeScope);
        Directory.CreateDirectory(targetDir);

        var id = Guid.NewGuid().ToString("N")[..10];
        var storedFileName = $"img_{DateTime.Now:yyyyMMdd_HHmmss}_{id}{extension}";
        var fullPath = Path.Combine(targetDir, storedFileName);
        File.WriteAllBytes(fullPath, encodedBytes);

        var relativePath = $"{safeScope}/{storedFileName}";
        return new ChatImageAttachment
        {
            Id = id,
            FileName = string.IsNullOrWhiteSpace(displayName) ? storedFileName : displayName.Trim(),
            RelativePath = relativePath,
            MimeType = mimeType,
            Width = finalWidth,
            Height = finalHeight,
            ByteSize = encodedBytes.LongLength,
            ExplicitFullPath = string.IsNullOrWhiteSpace(attachmentsRoot) ? null : fullPath
        };
    }

    private static (byte[] Bytes, string MimeType, string Extension, int Width, int Height) ResizeAndEncode(BitmapSource source)
    {
        // Always normalize to opaque Bgr24 first so Windows clipboard 32-bit BGRX (Alpha=0) bitmaps
        // and dirty zero-alpha PNGs never turn invisible/blank in WPF previews or zoom windows.
        var current = EnsureOpaqueBgr24(source, forceStripAllZeroAlpha: true);
        var maxEdge = Math.Max(current.PixelWidth, current.PixelHeight);
        if (maxEdge > MaxLongEdgePixels)
        {
            var scale = MaxLongEdgePixels / (double)maxEdge;
            var transformed = new TransformedBitmap(current, new ScaleTransform(scale, scale));
            if (transformed.CanFreeze)
            {
                transformed.Freeze();
            }

            current = transformed;
        }

        var width = current.PixelWidth;
        var height = current.PixelHeight;

        // Try lossless 24-bit opaque PNG first so small CAD dimensions, APDL colorbar legends, and error text stay razor sharp.
        var pngBytes = EncodePng(current);
        if (pngBytes.Length <= LosslessPngThresholdBytes)
        {
            return (pngBytes, "image/png", ".png", width, height);
        }

        // For larger photos / dense screenshots, encode high-quality JPEG.
        var jpegBytes = EncodeJpeg(current, JpegQualityLevel);
        if (jpegBytes.Length < pngBytes.Length)
        {
            return (jpegBytes, "image/jpeg", ".jpg", width, height);
        }

        return (pngBytes, "image/png", ".png", width, height);
    }

    private static BitmapSource RepairZeroAlphaIfNeeded(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32 &&
            source.Format != PixelFormats.Pbgra32 &&
            source.Format != PixelFormats.Bgr32)
        {
            return source;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return source;
        }

        var srcStride = width * 4;
        var srcPixels = new byte[srcStride * height];
        source.CopyPixels(srcPixels, srcStride, 0);

        var anyNonZeroAlpha = false;
        var anyNonZeroRgb = false;
        for (var i = 0; i < srcPixels.Length; i += 4)
        {
            if (srcPixels[i + 3] > 0)
            {
                anyNonZeroAlpha = true;
                break;
            }

            if (!anyNonZeroRgb && (srcPixels[i] != 0 || srcPixels[i + 1] != 0 || srcPixels[i + 2] != 0))
            {
                anyNonZeroRgb = true;
            }
        }

        if (anyNonZeroAlpha || !anyNonZeroRgb)
        {
            return source;
        }

        return ExtractBgr24Directly(srcPixels, width, height);
    }

    internal static BitmapSource EnsureOpaqueBgr24(BitmapSource source, bool forceStripAllZeroAlpha = true)
    {
        if (source.Format == PixelFormats.Bgr24)
        {
            return source;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return source;
        }

        if (source.Format == PixelFormats.Bgr32)
        {
            var stride32 = width * 4;
            var pixels32 = new byte[stride32 * height];
            source.CopyPixels(pixels32, stride32, 0);
            return ExtractBgr24Directly(pixels32, width, height);
        }

        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var srcStride = width * 4;
        var srcPixels = new byte[srcStride * height];
        bgra.CopyPixels(srcPixels, srcStride, 0);

        if (forceStripAllZeroAlpha)
        {
            var anyNonZeroAlpha = false;
            for (var i = 3; i < srcPixels.Length; i += 4)
            {
                if (srcPixels[i] > 0)
                {
                    anyNonZeroAlpha = true;
                    break;
                }
            }

            // Windows clipboard GDI 32-bit DIBs leave all Alpha bytes as 0 while keeping valid B,G,R bytes.
            // Strip byte 3 directly instead of alpha-blending against white (which would erase the entire image!).
            if (!anyNonZeroAlpha)
            {
                return ExtractBgr24Directly(srcPixels, width, height);
            }
        }

        return CompositeBgraOnWhiteToBgr24(srcPixels, width, height);
    }

    private static BitmapSource ExtractBgr24Directly(byte[] bgraPixels, int width, int height)
    {
        var srcStride = width * 4;
        var dstStride = width * 3;
        var dstPixels = new byte[dstStride * height];
        for (var y = 0; y < height; y++)
        {
            var srcRow = y * srcStride;
            var dstRow = y * dstStride;
            for (var x = 0; x < width; x++)
            {
                var si = srcRow + (x * 4);
                var di = dstRow + (x * 3);
                dstPixels[di] = bgraPixels[si];
                dstPixels[di + 1] = bgraPixels[si + 1];
                dstPixels[di + 2] = bgraPixels[si + 2];
            }
        }

        var opaque = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr24,
            null,
            dstPixels,
            dstStride);
        opaque.Freeze();
        return opaque;
    }

    private static BitmapSource CompositeBgraOnWhiteToBgr24(byte[] srcPixels, int width, int height)
    {
        var srcStride = width * 4;
        var dstStride = width * 3;
        var dstPixels = new byte[dstStride * height];
        for (var y = 0; y < height; y++)
        {
            var srcRow = y * srcStride;
            var dstRow = y * dstStride;
            for (var x = 0; x < width; x++)
            {
                var si = srcRow + (x * 4);
                var di = dstRow + (x * 3);
                var alpha = srcPixels[si + 3];
                if (alpha == 255)
                {
                    dstPixels[di] = srcPixels[si];
                    dstPixels[di + 1] = srcPixels[si + 1];
                    dstPixels[di + 2] = srcPixels[si + 2];
                }
                else
                {
                    var inv = 255 - alpha;
                    dstPixels[di] = (byte)(((srcPixels[si] * alpha) + (255 * inv)) / 255);
                    dstPixels[di + 1] = (byte)(((srcPixels[si + 1] * alpha) + (255 * inv)) / 255);
                    dstPixels[di + 2] = (byte)(((srcPixels[si + 2] * alpha) + (255 * inv)) / 255);
                }
            }
        }

        var flattened = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr24,
            null,
            dstPixels,
            dstStride);
        flattened.Freeze();
        return flattened;
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[] EncodeJpeg(BitmapSource bitmap, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 60, 95) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static string SanitizeScopeFolder(string? scopeFolder)
    {
        if (string.IsNullOrWhiteSpace(scopeFolder))
        {
            return "general";
        }

        var cleaned = SafeScopeSegmentRegex.Replace(scopeFolder.Trim(), "_").Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? "general" : cleaned[..Math.Min(48, cleaned.Length)];
    }

    private static string InferMimeTypeFromPath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/png"
        };
    }
}
