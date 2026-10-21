using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    // Static because the first page is built inside this constructor, before App.MainWindow is set.
    // Each entry names what its tool is in the middle of, or returns null when it is idle.
    private static readonly List<Func<string?>> Activities = [];
    // One-off work wrapped by Track, such as an export, listed until it finishes.
    private static readonly List<string> Tracked = [];
    // Open while the exit question is showing; completed when the answer is to keep running.
    private static TaskCompletionSource? _exitPrompt;
    private bool _exiting;

    /// <summary>Raised once when the window really closes, after any confirmation. Handlers stop their work and may block briefly to save.</summary>
    public static event Action? Exiting;

    /// <summary>Only one dialog can be open at a time, so a dialog that opens without a click waits on this first.</summary>
    public static Task ExitPromptClosed => _exitPrompt?.Task ?? Task.CompletedTask;

    /// <summary>Registers a check that closing runs; a non-null result is shown in the exit question.</summary>
    public static void TrackActivity(Func<string?> describe) => Activities.Add(describe);

    /// <summary>Shows <paramref name="what"/> in the exit question until <paramref name="work"/> finishes.</summary>
    public static async Task<T> Track<T>(string what, Task<T> work)
    {
        Tracked.Add(what);
        try { return await work; }
        finally { Tracked.Remove(what); }
    }

    public static async Task Track(string what, Task work)
    {
        Tracked.Add(what);
        try { await work; }
        finally { Tracked.Remove(what); }
    }

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Unpackaged apps get no taskbar or Alt+Tab icon from the exe resource alone.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RestoreWindowState();
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
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

    // EXIT

    // The close button, Alt+F4, the taskbar and a WM_CLOSE from another process all land here. Close() does not, so the
    // confirmed path below runs Exit itself. Logoff and shutdown do not raise this either.
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        var running = Activities.Select(a => a()).OfType<string>().Concat(Tracked).Distinct().ToList();
        if (running.Count == 0)
        {
            Exit();
            return;
        }
        // Set before the first await; closing again while the question is open just keeps it open.
        args.Cancel = true;
        if (_exitPrompt is not null) return;
        _exitPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (await ConfirmExitAsync(running))
        {
            Exit();
            Close();
            return;
        }
        var prompt = _exitPrompt;
        _exitPrompt = null;
        prompt.SetResult();
    }

    private async Task<bool> ConfirmExitAsync(List<string> running)
    {
        // A close from the taskbar can arrive while minimized, where the question would go unseen.
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        var root = Content.XamlRoot;
        // Only one dialog can be open at a time. A hidden question counts as cancelled; the ffmpeg download brings its progress back.
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            if (popup.Child is ContentDialog open) open.Hide();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Exit while work is in progress?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = string.Join(Environment.NewLine, running) + Environment.NewLine + Environment.NewLine
                     + "Exiting stops it now, and unfinished work is lost.",
            },
            PrimaryButtonText = "Exit",
            CloseButtonText = "Keep running",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void Exit()
    {
        _exiting = true;
        SaveWindowState();
        Exiting?.Invoke();
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
