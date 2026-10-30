using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace WinVideoTools;

/// <summary>A short message at the bottom of the window that goes away by itself.</summary>
public static class Toast
{
    private static Popup? _current;

    public static async void Show(XamlRoot root, string message)
    {
        // A newer toast replaces the one still showing instead of stacking on top of it.
        if (_current is not null) _current.IsOpen = false;

        var border = new Border
        {
            Child = new TextBlock { Text = message },
            Padding = new Thickness(16, 8, 16, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"],
        };
        var popup = new Popup { XamlRoot = root, Child = border, IsHitTestVisible = false };
        popup.ChildTransitions = [new PopupThemeTransition()];
        // The size is only known after layout, so center it once measured.
        border.SizeChanged += (_, e) =>
        {
            popup.HorizontalOffset = (root.Size.Width - e.NewSize.Width) / 2;
            popup.VerticalOffset = root.Size.Height - e.NewSize.Height - 48;
        };
        _current = popup;
        popup.IsOpen = true;

        await Task.Delay(TimeSpan.FromSeconds(2));
        popup.IsOpen = false;
        if (_current == popup) _current = null;
    }
}
