using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace WinVideoTools;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        var asm = typeof(App).Assembly;
        VersionText.Text = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var buildTime = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value;
        BuildTimeText.Text = DateTimeOffset.TryParse(buildTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToUniversalTime().ToString("dddd, MMMM d, yyyy HH:mm:ss 'UTC'", CultureInfo.CurrentCulture)
            : "unknown";
        RuntimeText.Text = RuntimeInformation.FrameworkDescription;
        _ = LoadFfmpegVersionAsync();
    }

    private async Task LoadFfmpegVersionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg.ExePath, "-hide_banner -version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            FfmpegText.Text = await proc.StandardOutput.ReadLineAsync() ?? "unknown";
            await proc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            FfmpegText.Text = $"Not available: {ex.Message}";
        }
    }
}
