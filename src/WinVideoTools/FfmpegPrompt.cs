using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinVideoTools;

/// <summary>Asks before downloading ffmpeg when it is missing or broken, at startup and again when a tool needs it.</summary>
public static class FfmpegPrompt
{
    private const string NotInstalled = "WinVideoTools uses ffmpeg to read and convert videos, and it is not installed.";

    /// <summary>Startup check: proves ffmpeg runs, and offers a fresh download when it does not.</summary>
    public static async Task StartupCheckAsync(XamlRoot root)
    {
        if (await Ffmpeg.VerifyAsync() is not { } problem) return;
        var reason = Ffmpeg.IsInstalled ? $"ffmpeg is installed but does not work:{Environment.NewLine}{problem}" : NotInstalled;
        if (!await AskAndDownloadAsync(root, reason)) return;
        if (await Ffmpeg.VerifyAsync() is { } still) await ShowErrorAsync(root, still);
    }

    /// <summary>Before a tool runs: true when ffmpeg is present, possibly after the user agreed to download it.</summary>
    public static async Task<bool> EnsureAsync(XamlRoot root) =>
        Ffmpeg.IsInstalled || await AskAndDownloadAsync(root, NotInstalled);

    private static async Task<bool> AskAndDownloadAsync(XamlRoot root, string reason)
    {
        var ask = new ContentDialog
        {
            XamlRoot = root,
            Title = "Download ffmpeg?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"{reason}{Environment.NewLine}{Environment.NewLine}"
                     + $"Download ffmpeg {Ffmpeg.Version} ({Ffmpeg.DownloadBytes / (1 << 20)} MB) from github.com/GyanD/codexffmpeg? "
                     + $"It is checked against a known SHA-256 hash and saved to {Path.GetDirectoryName(Ffmpeg.UserCopyPath)}.",
            },
            PrimaryButtonText = "Download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return false;

        using var cts = new CancellationTokenSource();
        var bar = new ProgressBar { Maximum = 1 };
        var status = new TextBlock { Text = "Starting..." };
        var progress = new ContentDialog
        {
            XamlRoot = root,
            Title = "Downloading ffmpeg",
            Content = new StackPanel { Spacing = 8, Children = { bar, status } },
            CloseButtonText = "Cancel",
        };
        progress.CloseButtonClick += (_, _) => cts.Cancel();
        var shown = progress.ShowAsync();

        string? error = null;
        try
        {
            await Ffmpeg.DownloadAsync(new Progress<double>(p =>
            {
                bar.Value = p;
                status.Text = $"{p * Ffmpeg.DownloadBytes / (1 << 20):0} of {Ffmpeg.DownloadBytes / (1 << 20)} MB";
            }), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the dialog; nothing to report.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            progress.Hide();
            await shown;
        }

        if (error is not null)
        {
            await ShowErrorAsync(root, error);
            return false;
        }
        return Ffmpeg.IsInstalled;
    }

    private static async Task ShowErrorAsync(XamlRoot root, string message) =>
        await new ContentDialog
        {
            XamlRoot = root,
            Title = "ffmpeg is not available",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        }.ShowAsync();
}
