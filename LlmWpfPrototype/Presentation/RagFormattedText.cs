using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LlmWpfPrototype.Services.Rag;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

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
    private static readonly Regex InlineToken = new(
        @"(?<formula>\\\([^\r\n]*?\\\)|\\\[[^\r\n]*?\\\]|\$\$[^\r\n]+?\$\$|(?<![\\$\w])\$(?![$\s])[^\r\n$]+\$(?![$\w]))"
        + @"|(?<code>`[^`\r\n]+`)|(?<strong>\*\*(?<stars>\S(?:.*?\S)?)\*\*|__(?<underscores>\S(?:.*?\S)?)__)"
        + @"|(?<citation>(?<!\\)\[(?<id>E[0-9]+)\])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly FontFamily MathFont = new("Cambria Math");
    private static readonly Brush CitationBrush = BrushFromRgb(15, 90, 153);
    private static readonly Brush FormulaBackground = BrushFromRgb(238, 244, 250);
    private static readonly Brush InlineCodeBackground = BrushFromRgb(241, 245, 249);
    private static readonly Brush InlineCodeForeground = BrushFromRgb(15, 90, 153);

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

    private static FlowDocument BuildDocument(string text, IReadOnlyList<LocalChatEvidence>? citations, bool hideSourceReferences)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), LineHeight = 22 };
        var sources = (citations ?? []).ToDictionary(source => source.Id, StringComparer.OrdinalIgnoreCase);
        var normalized = text.ReplaceLineEndings("\n");
        if (hideSourceReferences)
            normalized = HideReferencesInDisplay(normalized);
        var start = 0;
        foreach (Match match in DisplayBlock.Matches(normalized))
        {
            AddTextBlocks(document, normalized[start..match.Index], sources);
            var token = match.Groups["formula"].Value;
            var formula = FormulaRun(token);
            document.Blocks.Add(new Paragraph(formula)
            {
                Margin = new Thickness(0, 4, 0, 8),
                Padding = new Thickness(12, 5, 12, 5),
                Background = FormulaBackground
            });
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
        var afterBlank = false;
        var inCodeBlock = false;
        foreach (var line in text.Split('\n'))
        {
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

            var paragraph = new Paragraph { Margin = new Thickness(0, afterBlank && document.Blocks.Count > 0 ? 7 : 0, 0, 3) };
            afterBlank = false;
            var heading = Heading.Match(line);
            var list = ListItem.Match(line);
            if (inCodeBlock)
            {
                paragraph.Inlines.Add(new Run(line) { FontFamily = new FontFamily("Consolas") });
            }
            else if (heading.Success)
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

    private static void AddInlines(InlineCollection target, string text,
        IReadOnlyDictionary<string, LocalChatEvidence> sources, bool allowBold = true)
    {
        var position = 0;
        foreach (Match match in InlineToken.Matches(text))
        {
            if (position < match.Index) target.Add(new Run(text[position..match.Index]));
            if (match.Groups["formula"].Success)
            {
                target.Add(FormulaRun(match.Value));
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
            else if (match.Groups["citation"].Success)
            {
                var id = match.Groups["id"].Value;
                var found = sources.TryGetValue(id, out var source);
                var citation = new Run(found ? $"〔{CitationLabel(source!.Id)}〕" : $"〔未匹配依据 {id[1..]}〕")
                {
                    Foreground = found ? CitationBrush : Brushes.Firebrick,
                    FontWeight = FontWeights.SemiBold,
                    ToolTip = found ? source!.Source.Title : "该编号不对应本轮资料"
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

    private static Run FormulaRun(string token)
    {
        var delimiterLength = token.StartsWith('\\') || token.StartsWith("$$", StringComparison.Ordinal) ? 2 : 1;
        return RagFormulaText.TryFormat(token[delimiterLength..^delimiterLength], out var formatted)
            ? new Run(formatted) { FontFamily = MathFont }
            : new Run(token);
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
