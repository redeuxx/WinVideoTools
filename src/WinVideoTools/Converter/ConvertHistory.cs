using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinVideoTools.Converter;

/// <summary>What happened to a file. Output marks a file this app wrote, so adding its folder never queues it.</summary>
public enum HistoryOutcome { Converted, Discarded, Skipped, Output }

/// <summary>A processed file as it was when recorded; a different size or modified time means it changed since.</summary>
public sealed record HistoryEntry(HistoryOutcome Outcome, long Size, DateTime Modified, bool SkippedUpTo1080p = false);

/// <summary>
/// Every file the converter has finished with and every folder added, kept across sessions and lists, so a
/// processed file is not queued again until it changes. Used from the UI thread only; writes take a JSON copy.
/// </summary>
public sealed class ConvertHistory
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private sealed record Data(List<string>? Folders, Dictionary<string, HistoryEntry?>? Files);

    private readonly List<string> _folders = [];
    private readonly Dictionary<string, HistoryEntry> _files = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Folders => _folders;
    public int Count => _files.Count;

    public void AddFolder(string folder)
    {
        if (!_folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) _folders.Add(folder);
    }

    public void Record(string path, HistoryOutcome outcome, long size, DateTime modified, bool skippedUpTo1080p = false) =>
        _files[path] = new HistoryEntry(outcome, size, modified, skippedUpTo1080p);

    public HistoryEntry? Find(string path) => _files.GetValueOrDefault(path);

    /// <summary>
    /// True when the file was processed and has not changed since. A skip only counts while the options would skip
    /// it again, so turning Skip HEVC off brings those files back.
    /// </summary>
    public bool IsProcessed(string path, (long Size, DateTime Modified)? now, bool skipHevc, bool skipHevcUpTo1080p) =>
        _files.TryGetValue(path, out var e) && Same(e, now)
        && (e.Outcome != HistoryOutcome.Skipped || PresetConverter.StillSkipped(e.SkippedUpTo1080p, skipHevc, skipHevcUpTo1080p));

    /// <summary>True when the file was processed but is now a different size or age.</summary>
    public bool Changed(string path, (long Size, DateTime Modified)? now) =>
        now is not null && _files.TryGetValue(path, out var e) && !Same(e, now);

    private static bool Same(HistoryEntry e, (long Size, DateTime Modified)? now) =>
        now is { } n && n.Size == e.Size && n.Modified == e.Modified;

    /// <summary>Adds another history's folders and files; for a file in both, the other one's entry wins.</summary>
    public void Merge(ConvertHistory other)
    {
        foreach (var f in other._folders) AddFolder(f);
        foreach (var (path, entry) in other._files) _files[path] = entry;
    }

    public void Clear()
    {
        _folders.Clear();
        _files.Clear();
    }

    public string ToJson() => JsonSerializer.Serialize(new Data(_folders, _files.ToDictionary(f => f.Key, f => (HistoryEntry?)f.Value)), Json);

    // Written beside the target first, so a failed save never leaves a half-written history in place of a good one.
    public static void WriteJson(string path, string json)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Reads a history file. An imported one may come from elsewhere, so only absolute video paths and
    /// absolute folders are kept; entries only ever keep files out of the queue.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a convert history.</exception>
    public static ConvertHistory Read(string path)
    {
        Data? d;
        try
        {
            d = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Not a convert history file.", ex);
        }
        if (d?.Files is null) throw new InvalidDataException("Not a convert history file.");

        var h = new ConvertHistory();
        foreach (var f in d.Folders ?? []) if (ConvertSession.FullPath(f) is { } full) h.AddFolder(full);
        foreach (var (p, e) in d.Files)
            if (e is { Size: >= 0 } && Enum.IsDefined(e.Outcome) && ConvertSession.FullPath(p) is { } full && VideoFiles.IsVideo(full))
                h._files[full] = e;
        return h;
    }

    /// <summary>Size and last write time, or null when the file cannot be read. A FileInfo from a folder listing already holds both.</summary>
    public static (long Size, DateTime Modified)? Stat(FileInfo file)
    {
        try
        {
            return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
