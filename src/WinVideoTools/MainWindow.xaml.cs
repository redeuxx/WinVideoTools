using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinVideoTools.Converter;
using WinVideoTools.SimilarVideos;
using WinVideoTools.VideoVerifier;

namespace WinVideoTools;

public sealed partial class MainWindow : Window
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinVideoTools", "window.json");

    private sealed record WindowState(int X, int Y, int Width, int Height, bool Maximized);

    // Bounds while not maximized or minimized; AppWindow.Position/Size report the maximized rect otherwise.
    private RectInt32 _restoredBounds;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Unpackaged apps get no taskbar or Alt+Tab icon from the exe resource alone.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RestoreWindowState();
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += (_, _) => SaveWindowState();
        Nav.SelectedItem = Nav.MenuItems[0];
        // Every tool needs ffmpeg, so prove it runs at startup. Declining is fine: each tool asks again when used.
        if (Content is FrameworkElement root)
            root.Loaded += async (_, _) => await FfmpegPrompt.StartupCheckAsync(root.XamlRoot);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = (args.SelectedItem as NavigationViewItem)?.Tag switch
        {
            "About" => typeof(AboutPage),
            "SimilarVideos" => typeof(SimilarVideosPage),
            "Converter" => typeof(ConverterPage),
            _ => typeof(VideoVerifierPage),
        };
        ContentFrame.Navigate(page);
    }

    // WINDOW STATE

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange)
            && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _restoredBounds = new(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
    }

    private void RestoreWindowState()
    {
        WindowState? state = null;
        try
        {
            if (File.Exists(StatePath)) state = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(StatePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable or corrupt state just means default placement.
        }

        // Only trust saved bounds that are a sane size and still land on a connected display.
        var rect = state is { Width: >= 400 and <= 20000, Height: >= 300 and <= 20000 }
            ? new RectInt32(state.X, state.Y, state.Width, state.Height)
            : default;
        if (rect.Width > 0 && DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not null)
        {
            AppWindow.MoveAndResize(rect);
        }
        else
        {
            AppWindow.Resize(new(1200, 800));
        }
        _restoredBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

        // Activate() shows the window in its normal state, so maximizing earlier would be undone.
        if (state?.Maximized == true) Activated += MaximizeOnFirstActivation;
    }

    private void MaximizeOnFirstActivation(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MaximizeOnFirstActivation;
        if (AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
    }

    private void SaveWindowState()
    {
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        var b = _restoredBounds;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new WindowState(b.X, b.Y, b.Width, b.Height, maximized)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing window placement is not worth blocking close.
        }
    }
}
