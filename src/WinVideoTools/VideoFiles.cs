using System.Diagnostics;

namespace WinVideoTools;

public static class VideoFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".asf", ".flv",
        ".mpg", ".mpeg", ".m2v", ".ts", ".m2ts", ".mts", ".3gp", ".3g2", ".ogv", ".vob", ".rm", ".rmvb",
    };

    public static bool IsVideo(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Video files under <paramref name="folder"/>, sorted by path. Inaccessible folders are skipped.
    /// Size and modified time come from the enumeration itself, so reading them costs no extra disk access.
    /// <paramref name="found"/> hears the running count of videos now and then, for a progress line.
    /// </summary>
    public static List<FileInfo> Find(string folder, bool recurse, CancellationToken ct, IProgress<int>? found = null)
    {
        int seen = 0, videos = 0;
        return new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true })
            .Where(f =>
            {
                ct.ThrowIfCancellationRequested();
                var video = IsVideo(f.Name);
                if (video) videos++;
                // Throttled, since each report is a hop to the UI thread.
                if (++seen % 200 == 0) found?.Report(videos);
                return video;
            })
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when the file looks unchanged since it was listed with this size and time.</summary>
    public static bool IsUnchanged(FileInfo file, long size, DateTime modifiedUtc) =>
        file.Length == size && file.LastWriteTimeUtc == modifiedUtc;

    /// <summary>
    /// Opens the file in the user's default player. Paths come from a scan that already filtered to
    /// video extensions; re-check anyway so the shell never launches anything else.
    /// </summary>
    public static void Open(string path)
    {
        if (!IsVideo(path) || !File.Exists(path)) throw new FileNotFoundException("File not found.", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Opens the file's folder in Explorer with the file selected.</summary>
    public static void ShowInFolder(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("File not found.", path);
        // Explorer parses /select itself, so the path is quoted by hand. Windows names cannot hold a quote; refuse one anyway
        // so it can never end the argument early.
        if (path.Contains('"')) throw new ArgumentException("Path contains a quote.", nameof(path));
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
    }
}
