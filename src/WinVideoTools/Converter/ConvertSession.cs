using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinVideoTools.Converter;

public enum ConvertStatus { Queued, Converting, Done, Failed, Cancelled, Skipped }

/// <summary>One file's place in a saved queue: its outcome so far, as the list showed it.</summary>
public sealed record SessionItem(string Path, long InputSize, ConvertStatus Status, string? Summary, string? Decision, string? Details,
    string? OutputPath, long? OutputSize, double? SizeRatio, bool OriginalDeleted);

/// <summary>
/// A saved convert queue. Folders are the ones files were added from, so a rescan can pick up videos
/// that appeared there since.
/// </summary>
public sealed record ConvertSession(List<string> Folders, List<SessionItem> Items)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    // Written beside the target first, so a failed save never leaves a half-written session in place of a good one.
    public void Write(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Reads a session file. It may have been edited or come from elsewhere, so only absolute video paths
    /// are kept; later actions open and delete these paths. A file that was converting when saved is queued
    /// again, and an output that has since gone is forgotten.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a session.</exception>
    public static ConvertSession Read(string path)
    {
        ConvertSession? s;
        try
        {
            s = JsonSerializer.Deserialize<ConvertSession>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Not a convert session file.", ex);
        }
        if (s?.Items is null) throw new InvalidDataException("Not a convert session file.");

        static bool IsVideoPath(string? p) => p is not null && Path.IsPathFullyQualified(p) && VideoFiles.IsVideo(p);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = s.Items
            .Where(i => i is not null && IsVideoPath(i.Path) && seen.Add(Path.GetFullPath(i.Path)))
            .Select(i => i with
            {
                Path = Path.GetFullPath(i.Path),
                Status = i.Status == ConvertStatus.Converting ? ConvertStatus.Queued : i.Status,
                OutputPath = IsVideoPath(i.OutputPath) && File.Exists(i.OutputPath) ? Path.GetFullPath(i.OutputPath!) : null,
            })
            .ToList();
        var folders = (s.Folders ?? [])
            .Where(f => f is not null && Path.IsPathFullyQualified(f))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ConvertSession(folders, items);
    }
}
