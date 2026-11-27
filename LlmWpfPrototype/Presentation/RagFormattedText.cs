using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LlmWpfPrototype.Services.Rag;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace LlmWpfPrototype.Presentation;

// Only the view uses this formatter. The underlying answer and evidence stay intact.
public sealed class RagFormattedText : System.Windows.Controls.RichTextBox
{
    public static readonly DependencyProperty MarkupProperty = DependencyProperty.Register(
        nameof(Markup), typeof(string), typeof(RagFormattedText), new FrameworkPropertyMetadata("", RefreshDocument));

    public static readonly DependencyProperty CitationsProperty = DependencyProperty.Register(
        nameof(Citations), typeof(IReadOnlyList<LocalChatEvidence>), typeof(RagFormattedText),
        new FrameworkPropertyMetadata(null, RefreshDocument));

    public static readonly DependencyProperty HideSourceReferencesProperty = DependencyProperty.Register(
        nameof(HideSourceReferences), typeof(bool), typeof(RagFormattedText),
        new FrameworkPropertyMetadata(false, RefreshDocument));

    private static readonly Regex DisplayBlock = new(
        @"^[ \t]*(?<formula>\\\[[\s\S]*?\\\]|\$\$[\s\S]*?\$\$)[ \t]*$", RegexOptions.Multiline);
    private static readonly Regex Heading = new(@"^[ \t]{0,3}#{1,6}[ \t]+(?<body>\S.*)$");
    private static readonly Regex ListItem = new(@"^[ \t]*(?<mark>[-*+•]|\d{1,6}[.)])[ \t]+(?<body>\S.*)$");
    private static readonly Regex BlockQuoteLine = new(@"^[ \t]*>[ \t]?(?<body>.*)$");
    private static readonly Regex LocalMarkdownImageLine = new(
        @"^[ \t]*!\[(?<alt>[^\]\r\n]{1,180})\]\((?<path>[A-Za-z0-9_-]+/assets/[A-Za-z0-9_]+\.png)\)[ \t]*\r?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HorizontalRule = new(@"^[ \t]*(?:[-*_][ \t]*){3,}$");
    private static readonly Regex TableSeparatorLine = new(
        @"^[ \t]*\|?[ \t]*:?-{2,}:?[ \t]*(?:\|[ \t]*:?-{2,}:?[ \t]*)+\|?[ \t]*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SourceMetadata = new(
        @"(?im)^[ \t]*(?:Source|Topic|SourceType|Verified|Clause|Page|资料标注条款|条款号|页码|文件|原文位置)\s*[:：][^\n]*(?:\n|$)");
    private static readonly Regex NumberedHeading = new(
        @"(?m)^(?<prefix>[ \t]*#{1,6}[ \t]*)(?<number>\d+(?:\.\d+)*)(?:[ \t]+(?<title>[^\r\n]+))?[ \t]*$");
    private static readonly Regex ChapterReference = new(
        @"第\s*[0-9一二三四五六七八九十百]+(?:\.\d+)*\s*(?:章|节|条)");
    private static readonly Regex InlineClauseReference = new(
        @"(?:标准|条款)\s*\d+(?:\.\d+)+|附录\s*[A-Z](?![A-Za-z0-9])");
    private static readonly Regex PageReference = new(
        @"第\s*\d+(?:\s*[-–—至]\s*\d+)?\s*页");
    private static readonly Regex OpenAiWebCitationNoise = new(
        @"[ \t]*[\(（]\s*(?:\[[^\]\r\n]{1,160}\]\(\s*https?://[^)\r\n\s]*utm_source=[^)\r\n\s]*\s*\)(?:[\s,，、;；]*))+\s*[\)）]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InlineToken = new(
        @"(?<formula>\\\([^\r\n]*?\\\)|\\\[[^\r\n]*?\\\]|\$\$[^\r\n]+?\$\$|(?<![\\$A-Za-z0-9])\$(?![$\s])[^\r\n$]*?(?<![\s\\])\$(?![$A-Za-z0-9]))"
        + @"|(?<code>`[^`\r\n]+`)|(?<strong>\*\*(?<stars>\S(?:.*?\S)?)\*\*|__(?<underscores>\S(?:.*?\S)?)__)"
        + @"|(?<link>\[(?<linkText>[^\]\r\n]{1,160})\]\(\s*(?<linkUrl>https?://[^)\r\n\s]+)\s*\))"
        + @"|(?<citation>(?<!\\)\[(?<id>E[0-9]+)\])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly FontFamily MathFont = new("Cambria Math");
    private static readonly Brush CitationBrush = BrushFromRgb(15, 90, 153);
    private static readonly Brush FormulaBackground = BrushFromRgb(238, 244, 250);
    private static readonly Brush InlineCodeBackground = BrushFromRgb(241, 245, 249);
    private static readonly Brush InlineCodeForeground = BrushFromRgb(15, 90, 153);
    private static readonly Brush TableBorderBrush = BrushFromRgb(203, 213, 225);
    private static readonly Brush TableGridBrush = BrushFromRgb(226, 232, 240);
    private static readonly Brush TableHeaderBackground = BrushFromRgb(241, 245, 249);
    private static readonly Brush TableStripeBackground = BrushFromRgb(248, 250, 252);
    private static readonly Brush QuoteBorderBrush = BrushFromRgb(56, 189, 248);
    private static readonly Brush QuoteBackground = BrushFromRgb(240, 249, 255);
    private static readonly Brush QuoteForeground = BrushFromRgb(12, 74, 110);
    private static readonly Brush RuleBrush = BrushFromRgb(226, 232, 240);

    public string Markup
    {
        get => (string)GetValue(MarkupProperty);
        set => SetValue(MarkupProperty, value);
    }

    public IReadOnlyList<LocalChatEvidence>? Citations
    {
        get => (IReadOnlyList<LocalChatEvidence>?)GetValue(CitationsProperty);
        set => SetValue(CitationsProperty, value);
    }

    public bool HideSourceReferences
    {
        get => (bool)GetValue(HideSourceReferencesProperty);
        set => SetValue(HideSourceReferencesProperty, value);
    }

    public RagFormattedText()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 14;
        Document = BuildDocument("", null, false);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (VerticalScrollBarVisibility is ScrollBarVisibility.Hidden or ScrollBarVisibility.Disabled &&
            Parent is UIElement parent)
        {
            e.Handled = true;
            var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = this
            };
            parent.RaiseEvent(forwarded);
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    private static void RefreshDocument(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (RagFormattedText)sender;
        view.Document = BuildDocument(view.Markup ?? "", view.Citations, view.HideSourceReferences);
    }

    internal static string CitationLabel(string id) =>
        id.Length > 1 && (id[0] is 'E' or 'e') && id[1..].All(char.IsAsciiDigit)
            ? "依据 " + id[1..]
            : "依据 " + id;

    public static FlowDocument BuildDocument(string text, IReadOnlyList<LocalChatEvidence>? citations, bool hideSourceReferences)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), LineHeight = 22 };
        var sources = (citations ?? []).ToDictionary(source => source.Id, StringComparer.OrdinalIgnoreCase);
        var normalized = text.ReplaceLineEndings("\n");
        if (normalized.Contains("utm_source=", StringComparison.OrdinalIgnoreCase))
            normalized = OpenAiWebCitationNoise.Replace(normalized, string.Empty);
        if (hideSourceReferences)
            normalized = HideReferencesInDisplay(normalized);
        var start = 0;
        foreach (Match match in DisplayBlock.Matches(normalized))
        {
            AddTextBlocks(document, normalized[start..match.Index], sources);
            var token = match.Groups["formula"].Value;
            var formulaParagraph = new Paragraph
            {
                Margin = new Thickness(0, 4, 0, 8),
                Padding = new Thickness(12, 6, 12, 6),
                Background = FormulaBackground
            };
            AddFormulaInlines(formulaParagraph.Inlines, token);
            document.Blocks.Add(formulaParagraph);
            start = match.Index + match.Length;
        }
        AddTextBlocks(document, normalized[start..], sources);
        return document;
    }

    private static string HideReferencesInDisplay(string text)
    {
        var result = SourceMetadata.Replace(text, "");
        result = NumberedHeading.Replace(result, match =>
        {
            var title = match.Groups["title"].Value;
            return string.IsNullOrWhiteSpace(title) ? "" : match.Groups["prefix"].Value + title;
        });
        result = ChapterReference.Replace(result, "相关章节");
        result = InlineClauseReference.Replace(result, "相关条文");
        return PageReference.Replace(result, "相关内容");
    }

    private static void AddTextBlocks(FlowDocument document, string text,
        IReadOnlyDictionary<string, LocalChatEvidence> sources)
    {
        var lines = text.Split('\n');
        var afterBlank = false;
        var inCodeBlock = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) && !inCodeBlock)
            {
                afterBlank = true;
                continue;
            }

            if (inCodeBlock)
            {
                var codeParagraph = new Paragraph
                {
                    Margin = new Thickness(0, afterBlank && document.Blocks.Count > 0 ? 5 : 0, 0, 2)
                };
                afterBlank = false;
                codeParagraph.Inlines.Add(new Run(line) { FontFamily = new FontFamily("Consolas") });
                document.Blocks.Add(codeParagraph);
                continue;
            }

            // Detect and render Markdown tables natively as WPF FlowDocument Table.
            if (i + 1 < lines.Length && IsTableHeaderPair(line, lines[i + 1]))
            {
                var headerCells = SplitTableRow(line);
                var alignments = ParseTableAlignments(lines[i + 1], headerCells.Length);
                var dataRows = new List<string[]>();
                var rowIndex = i + 2;
                while (rowIndex < lines.Length && IsTableDataRow(lines[rowIndex]))
                {
                    dataRows.Add(SplitTableRow(lines[rowIndex]));
                    rowIndex++;
                }

                document.Blocks.Add(BuildTable(headerCells, alignments, dataRows, sources));
                afterBlank = false;
                i = rowIndex - 1;
                continue;
            }

            // Detect safe local knowledge-base figure (![caption](PACK_ID/assets/figure.png))
            var imageMatch = LocalMarkdownImageLine.Match(line);
            if (imageMatch.Success)
            {
                var altText = imageMatch.Groups["alt"].Value.Trim();
                var relativeAssetPath = imageMatch.Groups["path"].Value.Trim();
                document.Blocks.Add(BuildLocalFigureBlock(altText, relativeAssetPath));
                afterBlank = false;
                continue;
            }

            // Detect horizontal rule (--- / ***)
            if (HorizontalRule.IsMatch(line))
            {
                document.Blocks.Add(new Paragraph
                {
                    Margin = new Thickness(0, 6, 0, 6),
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    FontSize = 1,
                    LineHeight = 1
                });
                afterBlank = false;
                continue;
            }

            // Detect blockquote callout (> ...)
            var quote = BlockQuoteLine.Match(line);
            if (quote.Success)
            {
                var quoteParagraph = new Paragraph
                {
                    Margin = new Thickness(0, afterBlank && document.Blocks.Count > 0 ? 6 : 3, 0, 6),
                    Padding = new Thickness(10, 5, 10, 5),
                    Background = QuoteBackground,
                    BorderBrush = QuoteBorderBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Foreground = QuoteForeground
                };
                afterBlank = false;
                AddInlines(quoteParagraph.Inlines, quote.Groups["body"].Value.Trim(), sources);
                while (i + 1 < lines.Length)
                {
                    var nextQuote = BlockQuoteLine.Match(lines[i + 1]);
                    if (!nextQuote.Success) break;
                    i++;
                    quoteParagraph.Inlines.Add(new LineBreak());
                    AddInlines(quoteParagraph.Inlines, nextQuote.Groups["body"].Value.Trim(), sources);
                }
                document.Blocks.Add(quoteParagraph);
                continue;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, afterBlank && document.Blocks.Count > 0 ? 7 : 0, 0, 3) };
            afterBlank = false;
            var heading = Heading.Match(line);
            var list = ListItem.Match(line);
            if (heading.Success)
            {
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.FontSize = 15;
                AddInlines(paragraph.Inlines, heading.Groups["body"].Value, sources);
            }
            else if (list.Success)
            {
                var marker = list.Groups["mark"].Value;
                var indent = marker.Length > 1 ? 30 : 18;
                paragraph.Margin = new Thickness(indent, paragraph.Margin.Top, 0, 3);
                paragraph.TextIndent = -indent;
                paragraph.Inlines.Add(new Run(marker.Length == 1 ? "•  " : $"{marker}  "));
                AddInlines(paragraph.Inlines, list.Groups["body"].Value, sources);
            }
            else
            {
                AddInlines(paragraph.Inlines, line, sources);
            }
            document.Blocks.Add(paragraph);
        }
    }

    private static bool IsTableHeaderPair(string headerLine, string separatorLine)
    {
        if (!headerLine.Contains('|') || !TableSeparatorLine.IsMatch(separatorLine))
            return false;
        var headerCells = SplitTableRow(headerLine);
        var sepCells = SplitTableRow(separatorLine);
        return headerCells.Length >= 2 && sepCells.Length >= 2;
    }

    private static bool IsTableDataRow(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains('|'))
            return false;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#') || trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith('>'))
            return false;
        return SplitTableRow(line).Length >= 2;
    }

    private static string[] SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith(@"\|", StringComparison.Ordinal))
            trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(cell => cell.Replace(@"\|", "|").Trim()).ToArray();
    }

    private static TextAlignment[] ParseTableAlignments(string separatorLine, int columnCount)
    {
        var cells = SplitTableRow(separatorLine);
        var alignments = new TextAlignment[columnCount];
        for (var c = 0; c < columnCount; c++)
        {
            var token = c < cells.Length ? cells[c].Trim() : string.Empty;
            var left = token.StartsWith(':');
            var right = token.EndsWith(':');
            alignments[c] = (left, right) switch
            {
                (true, true) => TextAlignment.Center,
                (false, true) => TextAlignment.Right,
                _ => TextAlignment.Left
            };
        }
        return alignments;
    }

    private static Table BuildTable(
        string[] headerCells,
        TextAlignment[] alignments,
        IReadOnlyList<string[]> dataRows,
        IReadOnlyDictionary<string, LocalChatEvidence> sources)
    {
        var columnCount = Math.Max(headerCells.Length, dataRows.Count > 0 ? dataRows.Max(r => r.Length) : 0);
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 6, 0, 10),
            BorderBrush = TableBorderBrush,
            BorderThickness = new Thickness(1),
            FontSize = 13
        };
        for (var c = 0; c < columnCount; c++)
            table.Columns.Add(new TableColumn());

        var headerGroup = new TableRowGroup();
        var headerRow = new TableRow { Background = TableHeaderBackground };
        for (var c = 0; c < columnCount; c++)
        {
            var text = c < headerCells.Length ? headerCells[c] : string.Empty;
            var p = new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = 20,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = c < alignments.Length ? alignments[c] : TextAlignment.Left
            };
            AddInlines(p.Inlines, text, sources);
            headerRow.Cells.Add(new TableCell(p)
            {
                Padding = new Thickness(8, 5, 8, 5),
                BorderBrush = TableBorderBrush,
                BorderThickness = new Thickness(0, 0, c + 1 < columnCount ? 1 : 0, 1)
            });
        }
        headerGroup.Rows.Add(headerRow);
        table.RowGroups.Add(headerGroup);

        if (dataRows.Count > 0)
        {
            var bodyGroup = new TableRowGroup();
            for (var r = 0; r < dataRows.Count; r++)
            {
                var rowCells = dataRows[r];
                var row = new TableRow
                {
                    Background = r % 2 == 1 ? TableStripeBackground : Brushes.White
                };
                for (var c = 0; c < columnCount; c++)
                {
                    var text = c < rowCells.Length ? rowCells[c] : string.Empty;
                    var p = new Paragraph
                    {
                        Margin = new Thickness(0),
                        LineHeight = 20,
                        TextAlignment = c < alignments.Length ? alignments[c] : TextAlignment.Left
                    };
                    AddInlines(p.Inlines, text, sources);
                    row.Cells.Add(new TableCell(p)
                    {
                        Padding = new Thickness(8, 4.5, 8, 4.5),
                        BorderBrush = TableGridBrush,
                        BorderThickness = new Thickness(0, 0, c + 1 < columnCount ? 1 : 0, r + 1 < dataRows.Count ? 1 : 0)
                    });
                }
                bodyGroup.Rows.Add(row);
            }
            table.RowGroups.Add(bodyGroup);
        }

        return table;
    }

    private static void AddInlines(InlineCollection target, string text,
        IReadOnlyDictionary<string, LocalChatEvidence> sources, bool allowBold = true)
    {
        var position = 0;
        foreach (Match match in InlineToken.Matches(text))
        {
            if (position < match.Index) target.Add(new Run(text[position..match.Index]));
            if (match.Groups["formula"].Success)
            {
                AddFormulaInlines(target, match.Value);
            }
            else if (match.Groups["code"].Success)
            {
                target.Add(new Run(" " + match.Value[1..^1] + " ")
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = InlineCodeBackground,
                    Foreground = InlineCodeForeground
                });
            }
            else if (match.Groups["strong"].Success && allowBold)
            {
                var bold = new Bold();
                var content = match.Groups["stars"].Success ? match.Groups["stars"] : match.Groups["underscores"];
                AddInlines(bold.Inlines, content.Value, sources, allowBold: false);
                target.Add(bold);
            }
            else if (match.Groups["link"].Success)
            {
                var label = match.Groups["linkText"].Value.Trim();
                var url = match.Groups["linkUrl"].Value.Trim();
                if (!url.Contains("utm_source=", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(label))
                {
                    target.Add(new Run(label)
                    {
                        Foreground = CitationBrush,
                        TextDecorations = TextDecorations.Underline,
                        ToolTip = url
                    });
                }
            }
            else if (match.Groups["citation"].Success)
            {
                var id = match.Groups["id"].Value;
                var found = sources.TryGetValue(id, out var source);
                var citation = new Run(found ? $"〔{CitationLabel(source!.Id)}〕" : $"〔未匹配依据 {id[1..]}〕")
                {
                    Foreground = found ? CitationBrush : Brushes.Firebrick,
                    FontWeight = FontWeights.SemiBold,
                    ToolTip = found ? $"{source!.StandardLabel} · {source.Source.Title}" : "该编号不对应本轮资料"
                };
                target.Add(citation);
            }
            else
            {
                target.Add(new Run(match.Value));
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length) target.Add(new Run(text[position..]));
    }

    private static void AddFormulaInlines(InlineCollection target, string token)
    {
        var delimiterLength = token.StartsWith('\\') || token.StartsWith("$$", StringComparison.Ordinal) ? 2 : 1;
        if (RagFormulaText.TryFormat(token[delimiterLength..^delimiterLength], out var formatted))
        {
            var lines = formatted.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) target.Add(new LineBreak());
                target.Add(new Run(lines[i]) { FontFamily = MathFont });
            }
            return;
        }

        target.Add(new Run(token));
    }

    private static Block BuildLocalFigureBlock(string altText, string relativeAssetPath)
    {
        var fullPath = TryResolveSafeKnowledgeAssetPath(relativeAssetPath);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            var fallback = new Paragraph
            {
                Margin = new Thickness(0, 4, 0, 6),
                Foreground = BrushFromRgb(71, 85, 105),
                FontSize = 12.5
            };
            fallback.Inlines.Add(new Run($"〔规范附图：{altText}〕"));
            return fallback;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var imageControl = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxHeight = 360,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = $"{altText}（点击放大查看高清附图）"
            };
            RenderOptions.SetBitmapScalingMode(imageControl, BitmapScalingMode.HighQuality);
            imageControl.MouseLeftButtonUp += (_, args) =>
            {
                args.Handled = true;
                ShowZoomableFigureWindow(altText, bitmap);
            };

            var caption = new TextBlock
            {
                Text = $"{altText}  ·  点击图片放大查看",
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 12,
                Foreground = BrushFromRgb(71, 85, 105),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(imageControl);
            stack.Children.Add(caption);

            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = TableBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 8),
                Child = stack
            };

            return new BlockUIContainer(card)
            {
                Margin = new Thickness(0, 4, 0, 8)
            };
        }
        catch
        {
            var fallback = new Paragraph
            {
                Margin = new Thickness(0, 4, 0, 6),
                Foreground = BrushFromRgb(71, 85, 105),
                FontSize = 12.5
            };
            fallback.Inlines.Add(new Run($"〔规范附图：{altText}〕"));
            return fallback;
        }
    }

    public static bool OpenLocalKnowledgeFigurePreview(string title, string relativeAssetPath)
    {
        var bitmap = TryLoadKnowledgeFigureBitmap(relativeAssetPath);
        if (bitmap is null)
        {
            return false;
        }

        ShowZoomableFigureWindow(title, bitmap);
        return true;
    }

    internal static BitmapSource? TryLoadKnowledgeFigureBitmap(string? relativeAssetPath, int decodePixelWidth = 0)
    {
        var fullPath = TryResolveSafeKnowledgeAssetPath(relativeAssetPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth > 0)
            {
                bitmap.DecodePixelWidth = decodePixelWidth;
            }
            bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public static void OpenKnowledgeFigureGalleryWindow(IReadOnlyList<LocalKnowledgeFigureItem> figures)
    {
        if (figures.Count == 0)
        {
            return;
        }

        var groups = figures
            .Select(f => f.StandardLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var activeFilter = "全部";

        var filterPanel = new WrapPanel { Margin = new Thickness(14, 10, 14, 8) };
        var cardsPanel = new WrapPanel { Margin = new Thickness(14, 6, 14, 14) };

        void RebuildCards()
        {
            cardsPanel.Children.Clear();
            var visible = string.Equals(activeFilter, "全部", StringComparison.Ordinal)
                ? figures
                : figures.Where(f => string.Equals(f.StandardLabel, activeFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var fig in visible)
            {
                var thumb = TryLoadKnowledgeFigureBitmap(fig.RelativeAssetPath, decodePixelWidth: 560);
                var img = new Image
                {
                    Source = thumb,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 310,
                    MaxHeight = 190,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

                var badge = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(6, 1.5, 6, 1.5),
                    CornerRadius = new CornerRadius(5),
                    Background = BrushFromRgb(224, 242, 254),
                    BorderBrush = BrushFromRgb(186, 230, 253),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = new TextBlock
                    {
                        Text = fig.StandardLabel,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = BrushFromRgb(3, 105, 161)
                    }
                };

                var titleBlock = new TextBlock
                {
                    Text = fig.Title,
                    Margin = new Thickness(0, 8, 0, 2),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushFromRgb(15, 23, 42),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 310
                };

                var cue = new TextBlock
                {
                    Text = "点击查看大图（支持 Ctrl + 滚轮缩放）",
                    FontSize = 11,
                    Foreground = BrushFromRgb(2, 132, 199)
                };

                var body = new StackPanel();
                body.Children.Add(badge);
                body.Children.Add(new Border
                {
                    Background = Brushes.White,
                    BorderBrush = BrushFromRgb(226, 232, 240),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6),
                    MinHeight = 150,
                    Child = img
                });
                body.Children.Add(titleBlock);
                body.Children.Add(cue);

                var cardBtn = new System.Windows.Controls.Button
                {
                    Width = 340,
                    Margin = new Thickness(0, 0, 12, 12),
                    Padding = new Thickness(11, 9, 11, 9),
                    Background = Brushes.White,
                    BorderBrush = BrushFromRgb(203, 213, 225),
                    BorderThickness = new Thickness(1),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    Cursor = Cursors.Hand,
                    Content = body
                };
                var captured = fig;
                cardBtn.Click += (_, _) => OpenLocalKnowledgeFigurePreview(captured.Title, captured.RelativeAssetPath);
                cardsPanel.Children.Add(cardBtn);
            }
        }

        void RebuildFilters()
        {
            filterPanel.Children.Clear();
            var allLabels = new List<(string Key, string Label)> { ("全部", $"全部（{figures.Count}）") };
            foreach (var g in groups)
            {
                var count = figures.Count(f => string.Equals(f.StandardLabel, g, StringComparison.OrdinalIgnoreCase));
                allLabels.Add((g, $"{g}（{count}）"));
            }

            foreach (var (key, label) in allLabels)
            {
                var selected = string.Equals(activeFilter, key, StringComparison.OrdinalIgnoreCase);
                var pill = new System.Windows.Controls.Button
                {
                    Content = label,
                    Margin = new Thickness(0, 0, 8, 6),
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 12,
                    FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = selected ? Brushes.White : BrushFromRgb(51, 65, 85),
                    Background = selected ? BrushFromRgb(2, 132, 199) : Brushes.White,
                    BorderBrush = selected ? BrushFromRgb(2, 132, 199) : BrushFromRgb(203, 213, 225),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                var capturedKey = key;
                pill.Click += (_, _) =>
                {
                    activeFilter = capturedKey;
                    RebuildFilters();
                    RebuildCards();
                };
                filterPanel.Children.Add(pill);
            }
        }

        RebuildFilters();
        RebuildCards();

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = cardsPanel
        };

        var root = new DockPanel { Background = BrushFromRgb(248, 250, 252) };
        DockPanel.SetDock(filterPanel, Dock.Top);
        root.Children.Add(filterPanel);
        root.Children.Add(scroller);

        var galleryWindow = new Window
        {
            Title = $"本地工程规范高清矢量图库（共 {figures.Count} 张）",
            Width = Math.Min(1140, SystemParameters.WorkArea.Width * 0.88),
            Height = Math.Min(820, SystemParameters.WorkArea.Height * 0.86),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = BrushFromRgb(248, 250, 252),
            Content = root
        };
        galleryWindow.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                galleryWindow.Close();
            }
        };
        galleryWindow.Show();
    }

    internal static string? TryResolveSafeKnowledgeAssetPath(string relativeAssetPath)
    {
        if (string.IsNullOrWhiteSpace(relativeAssetPath) ||
            relativeAssetPath.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var normalizedRel = relativeAssetPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var kbRoot = new KnowledgeBaseLoader().ResolveKnowledgeBaseRootPath();
        var fullRoot = Path.GetFullPath(kbRoot);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalizedRel));
        if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(candidate) ? candidate : null;
    }

    public static void OpenImageZoomWindow(string title, string relativeOrAbsolutePath)
    {
        var bitmap = LlmWpfPrototype.Services.Chat.ChatImageAttachmentService.TryLoadBitmap(relativeOrAbsolutePath);
        if (bitmap is null)
        {
            return;
        }

        ShowZoomableFigureWindow(
            string.IsNullOrWhiteSpace(title) ? "图片高清预览" : title,
            bitmap,
            windowTitlePrefix: "图片高清预览");
    }

    private static void ShowZoomableFigureWindow(string title, BitmapSource bitmap, string windowTitlePrefix = "高清矢量工程图预览")
    {
        var zoomScale = 1.0;
        var scaleTransform = new ScaleTransform(1.0, 1.0);
        var zoomImage = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = 1040,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = scaleTransform
        };
        RenderOptions.SetBitmapScalingMode(zoomImage, BitmapScalingMode.HighQuality);

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.White,
            Padding = new Thickness(16),
            Content = zoomImage
        };

        var zoomLabel = new TextBlock
        {
            Text = "100%",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0),
            Foreground = BrushFromRgb(15, 90, 153),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5
        };

        void ApplyZoom(double next)
        {
            zoomScale = Math.Clamp(next, 0.5, 3.5);
            scaleTransform.ScaleX = zoomScale;
            scaleTransform.ScaleY = zoomScale;
            zoomLabel.Text = $"{Math.Round(zoomScale * 100):0}%";
        }

        scroller.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                ApplyZoom(zoomScale + (e.Delta > 0 ? 0.2 : -0.2));
            }
        };

        var zoomOutBtn = new System.Windows.Controls.Button { Content = "缩小 -", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        zoomOutBtn.Click += (_, _) => ApplyZoom(zoomScale - 0.25);
        var resetBtn = new System.Windows.Controls.Button { Content = "适合窗口 100%", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        resetBtn.Click += (_, _) => ApplyZoom(1.0);
        var zoomInBtn = new System.Windows.Controls.Button { Content = "放大 +", Padding = new Thickness(10, 3, 10, 3) };
        zoomInBtn.Click += (_, _) => ApplyZoom(zoomScale + 0.25);

        var hintLabel = new TextBlock
        {
            Text = "提示：按住 Ctrl + 鼠标滚轮可自由缩放 · Esc 关闭窗口",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushFromRgb(100, 116, 139),
            FontSize = 12
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 8, 12, 8)
        };
        toolbar.Children.Add(zoomOutBtn);
        toolbar.Children.Add(resetBtn);
        toolbar.Children.Add(zoomInBtn);
        toolbar.Children.Add(zoomLabel);
        toolbar.Children.Add(hintLabel);

        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        layout.Children.Add(scroller);

        var previewWindow = new Window
        {
            Title = string.IsNullOrWhiteSpace(title) ? windowTitlePrefix : $"{windowTitlePrefix} · {title}",
            Width = Math.Min(1160, SystemParameters.WorkArea.Width * 0.88),
            Height = Math.Min(860, SystemParameters.WorkArea.Height * 0.88),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = BrushFromRgb(248, 250, 252),
            Content = layout
        };
        previewWindow.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                previewWindow.Close();
            }
        };
        previewWindow.Show();
    }

    private static Brush BrushFromRgb(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}

public sealed class RagCitationLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string id ? RagFormattedText.CitationLabel(id) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LocalKnowledgeAssetPathToBitmapConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string relativePath ? RagFormattedText.TryLoadKnowledgeFigureBitmap(relativePath, decodePixelWidth: 680) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RagCitationStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string status || string.IsNullOrWhiteSpace(status) ||
            status.StartsWith("有效引用编号：", StringComparison.Ordinal))
            return string.Empty;
        if (status.StartsWith("引用缺失：", StringComparison.Ordinal))
            return "回答没有标明可对应本轮资料的引用，不能据此视为已核验结论。";
        return Regex.Replace(status, @"\bE(\d+)\b", "依据 $1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
