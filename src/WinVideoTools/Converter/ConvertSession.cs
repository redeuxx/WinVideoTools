using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinVideoTools.Converter;

public enum ConvertStatus { Queued, Converting, Done, Failed, Cancelled, Skipped }

/// <summary>One file's place in a saved queue: its outcome so far, as the list showed it.</summary>
public sealed record SessionItem(string Path, long InputSize, ConvertStatus Status, string? Summary, string? Decision, string? Details,
    string? OutputPath, long? OutputSize, double? SizeRatio, bool OriginalDeleted);

/// <summary>
/// The preset and options a queue converts with. "Shut down when finished" is left out on purpose,
/// so a reopened session never shuts the machine down unasked.
/// </summary>
public sealed record ConvertOptions(string? Preset, string? OutputFolder, bool SameFolder, bool SkipHevc, bool SkipHevcUpTo1080p,
    bool DeleteLarger, bool DeleteOriginal, bool AutoScroll);

/// <summary>
/// A saved convert queue. Folders are the ones files were added from, so a rescan can pick up videos
/// that appeared there since. Options is null in sessions saved before options were.
/// </summary>
public sealed record ConvertSession(List<string> Folders, List<SessionItem> Items, ConvertOptions? Options = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    // Written beside the target first, so a failed save never leaves a half-written session in place of a good one.
    public void Write(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Reads a session file. It may have been edited or come from elsewhere, so only absolute video paths
    /// are kept; later actions open and delete these paths. A saved "Delete original" is still confirmed
    /// at every Convert. A file that was converting when saved is queued
    /// again, and an output that has since gone is forgotten.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a session.</exception>
    public static ConvertSession Read(string path)
    {
        ConvertSession? s;
        try
        {
            s = JsonSerializer.Deserialize<ConvertSession>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Not a convert session file.", ex);
        }
        if (s?.Items is null) throw new InvalidDataException("Not a convert session file.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<SessionItem>();
        foreach (var i in s.Items)
        {
            if (i is null || FullPath(i.Path) is not { } p || !VideoFiles.IsVideo(p) || !seen.Add(p)) continue;
            // Network outputs are kept unchecked; Open checks them when the user asks.
            var output = FullPath(i.OutputPath) is { } o && VideoFiles.IsVideo(o) && (IsRemote(o) || File.Exists(o)) ? o : null;
            items.Add(i with { Path = p, Status = i.Status == ConvertStatus.Converting ? ConvertStatus.Queued : i.Status, OutputPath = output });
        }
        var folders = (s.Folders ?? []).Select(FullPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Conversions write into the output folder, so like the file paths it must be absolute.
        var options = s.Options is { } opt ? opt with { OutputFolder = FullPath(opt.OutputFolder) ?? "" } : null;
        return new ConvertSession(folders, items, options);
    }

    // Null for anything but a plain absolute path. GetFullPath throws on characters such as NUL, which would otherwise crash the load.
    private static string? FullPath(string? p)
    {
        if (p is null || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(p)) return null;
        try { return Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    // UNC and device paths. Probing one while loading would contact whatever server the file names and send it Windows credentials.
    private static bool IsRemote(string p) => p.StartsWith(@"\\", StringComparison.Ordinal);
}
