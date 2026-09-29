using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace WinVideoTools;

/// <summary>
/// Where ffmpeg.exe lives, and a pinned, hash-checked download for when it is missing. A copy
/// bundled next to the app wins; otherwise a per-user copy under %LOCALAPPDATA% is used, since the
/// app folder may be read-only (Program Files).
/// </summary>
public static class Ffmpeg
{
    // Pinned release. To upgrade, change all three together: version, SHA-256 of the zip, and its size.
    public const string Version = "9.0.2";
    internal const string Url = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-full_build.zip";
    internal const string Sha256 = "759d0a9831c436a0eb331ad36f236c06cb04aaa0005da46571f0e9d3d9206f6b";
    public const long DownloadBytes = 257_736_868;

    private static readonly string BundledPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    public static string UserCopyPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinVideoTools", "ffmpeg", "ffmpeg.exe");

    // Set when the bundled copy fails to run, so a freshly downloaded per-user copy takes over.
    private static bool _bundledBroken;

    public static string ExePath => File.Exists(BundledPath) && !_bundledBroken ? BundledPath : UserCopyPath;

    public static bool IsInstalled => File.Exists(ExePath);

    /// <summary>
    /// Runs ffmpeg -version to prove ffmpeg actually starts, not just that the file exists.
    /// Returns null when it works, otherwise why not. Falls back from a broken bundled copy to the per-user one.
    /// </summary>
    public static async Task<string?> VerifyAsync()
    {
        string? bundledError = null;
        if (File.Exists(BundledPath))
        {
            bundledError = await ProbeAsync(BundledPath).ConfigureAwait(false);
            if (bundledError is null) return null;
            _bundledBroken = true;
        }
        if (File.Exists(UserCopyPath)) return await ProbeAsync(UserCopyPath).ConfigureAwait(false);
        return bundledError ?? "ffmpeg is not installed.";
    }

    // Generous timeout: the first start of a large exe can be slow while antivirus scans it.
    internal static async Task<string?> ProbeAsync(string exe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process did not start.");
            try
            {
                var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
                var first = await proc.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                return proc.ExitCode == 0 && first?.StartsWith("ffmpeg version", StringComparison.Ordinal) == true
                    ? null
                    : $"{exe} did not report an ffmpeg version (exit code {proc.ExitCode}).";
            }
            finally
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
        }
        catch (OperationCanceledException)
        {
            return $"{exe} did not respond within 30 seconds.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"{exe} could not be started: {ex.Message}";
        }
    }

    // No overall timeout: the download is large and the user can cancel it.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Downloads the pinned build into <see cref="UserCopyPath"/>. Progress is 0 to 1.</summary>
    public static Task DownloadAsync(IProgress<double>? progress, CancellationToken ct) =>
        DownloadAsync(Url, Sha256, DownloadBytes, Path.GetDirectoryName(UserCopyPath)!, progress, ct);

    internal static async Task DownloadAsync(string url, string sha256, long size, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var zipPath = Path.Combine(folder, "download.zip.part");
        try
        {
            // Hash while streaming, and stop at the expected size, so a wrong or endless response
            // can neither be used nor fill the disk.
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > size) throw new InvalidDataException("The download is larger than the expected ffmpeg archive.");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((double)total / size);
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded ffmpeg archive failed its SHA-256 check, so it was not used.");
            }

            // Only these two entries are read, and they are written to names chosen here, never to paths from the archive.
            using var zip = ZipFile.OpenRead(zipPath);
            var exe = zip.Entries.SingleOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.Ordinal))
                      ?? throw new InvalidDataException("The ffmpeg archive does not contain bin/ffmpeg.exe.");
            Extract(exe, Path.Combine(folder, "ffmpeg.exe"));
            if (zip.Entries.FirstOrDefault(e => e.FullName.Count(c => c == '/') == 1 && e.Name == "LICENSE") is { } license)
                Extract(license, Path.Combine(folder, "LICENSE.txt"));
        }
        finally
        {
            try { File.Delete(zipPath); } catch (IOException) { }
        }
    }

    // Write then swap, so a failure never leaves a half-written ffmpeg.exe that looks installed.
    private static void Extract(ZipArchiveEntry entry, string dest)
    {
        var temp = dest + ".part";
        entry.ExtractToFile(temp, overwrite: true);
        File.Move(temp, dest, overwrite: true);
    }
}
