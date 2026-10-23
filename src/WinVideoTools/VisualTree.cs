using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinVideoTools;

public static class VisualTree
{
    /// <summary>The first descendant of type T, depth first; reaches parts of a control's template such as its ScrollViewer.</summary>
    public static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? FindChild<T>(child)) is { } found) return found;
        }
        return null;
    }
}
