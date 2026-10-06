using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinVideoTools;

// Lays children out left to right and starts a new line when the next one does not fit, so toolbars stay
// usable in a narrow window instead of clipping. WinUI has no built-in wrap panel.
// Nest one inside another to keep related controls together: an inner panel moves to a new line as a unit
// and only wraps itself when a whole line is too narrow for it.
public sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;
    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return Flow(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Flow(finalSize.Width, arrange: true);
        return finalSize;
    }

    // Each line is placed once its height is known, so a child's VerticalAlignment works within its line
    // the same way it does in a horizontal StackPanel.
    private Size Flow(double width, bool arrange)
    {
        var line = new List<UIElement>();
        double y = 0, lineWidth = 0, lineHeight = 0, maxWidth = 0;

        void EndLine()
        {
            if (line.Count == 0) return;
            if (arrange)
            {
                double x = 0;
                foreach (var child in line)
                {
                    child.Arrange(new Rect(x, y, child.DesiredSize.Width, lineHeight));
                    x += child.DesiredSize.Width + HorizontalSpacing;
                }
            }
            maxWidth = Math.Max(maxWidth, lineWidth);
            y += lineHeight + VerticalSpacing;
            line.Clear();
            lineWidth = lineHeight = 0;
        }

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            var needed = line.Count == 0 ? size.Width : lineWidth + HorizontalSpacing + size.Width;
            // The 1px slack absorbs layout rounding, which can hand Arrange a hair less width than Measure saw.
            if (line.Count > 0 && needed > width + 1)
            {
                EndLine();
                needed = size.Width;
            }
            line.Add(child);
            lineWidth = needed;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        EndLine();

        return new Size(maxWidth, y > 0 ? y - VerticalSpacing : 0);
    }
}
