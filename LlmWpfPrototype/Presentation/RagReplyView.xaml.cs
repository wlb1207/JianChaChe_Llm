using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LlmWpfPrototype.Presentation;

public partial class RagReplyView : System.Windows.Controls.UserControl
{
    private const double CollapsedEvidenceMaxHeight = 280d;

    public RagReplyView() => InitializeComponent();

    private void DirectFigureCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Services.Rag.LocalKnowledgeFigureItem fig })
        {
            RagFormattedText.OpenLocalKnowledgeFigurePreview(fig.Title, fig.RelativeAssetPath);
        }
    }

    private void EvidenceScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        // If the inner evidence viewer has room to scroll in this direction, consume the wheel smoothly;
        // otherwise bubble it out to the parent chat ScrollViewer so the user never gets stuck.
        if (scrollViewer.ScrollableHeight > 1)
        {
            var scrollingUp = e.Delta > 0;
            var scrollingDown = e.Delta < 0;
            var canScrollUp = scrollViewer.VerticalOffset > 0.5;
            var canScrollDown = scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight - 0.5;
            if ((scrollingUp && canScrollUp) || (scrollingDown && canScrollDown))
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta * 0.45);
                e.Handled = true;
                return;
            }
        }

        if (VisualTreeHelper.GetParent(scrollViewer) is UIElement parent)
        {
            e.Handled = true;
            var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = scrollViewer
            };
            parent.RaiseEvent(forwarded);
        }
    }

    private void ToggleEvidenceFullHeightButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        DependencyObject? current = button;
        while (current is not null and not StackPanel)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is not StackPanel container)
        {
            return;
        }

        var scrollViewer = FindDescendant<ScrollViewer>(container);
        if (scrollViewer is null)
        {
            return;
        }

        var isExpanded = double.IsPositiveInfinity(scrollViewer.MaxHeight);
        if (isExpanded)
        {
            scrollViewer.MaxHeight = CollapsedEvidenceMaxHeight;
            button.Content = "展开全文 ▾";
        }
        else
        {
            scrollViewer.MaxHeight = double.PositiveInfinity;
            button.Content = "收起高度 ▴";
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T target)
            {
                return target;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
