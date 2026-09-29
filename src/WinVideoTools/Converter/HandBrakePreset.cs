using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinVideoTools.Converter;

/// <summary>
/// One HandBrake preset, kept as the raw JSON object HandBrake wrote so that export round-trips
/// every key, including ones this app does not understand.
/// </summary>
public sealed class HandBrakePreset(JsonObject json, JsonObject versions)
{
    // Big enough for HandBrake's full presets.json with every built-in preset; a limit so a bad pick cannot eat memory.
    private const long MaxFileBytes = 10 * 1024 * 1024;

    public JsonObject Json { get; } = json;

    // VersionMajor/Minor/Micro from the file it came from; HandBrake refuses imports without them.
    private JsonObject Versions { get; } = versions;

    public string Name => Str("PresetName", "").Trim() is { Length: > 0 } n ? n : "Unnamed preset";

    public override string ToString() => Name;

    public string Str(string key, string fallback = "") =>
        Json[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    public double Num(string key, double fallback = 0) =>
        Json[key] is JsonValue v && (v.TryGetValue<double>(out var d) || (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out d)))
            && double.IsFinite(d) ? d : fallback;

    public bool Bool(string key, bool fallback = false) =>
        Json[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public List<string> StrList(string key) =>
        Json[key] is JsonArray a ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];

    public List<JsonObject> ObjList(string key) =>
        Json[key] is JsonArray a ? a.OfType<JsonObject>().ToList() : [];

    /// <summary>The preset alone, in HandBrake's export format, so HandBrake can import it back.</summary>
    public string ToFileJson()
    {
        var root = new JsonObject { ["PresetList"] = new JsonArray(Json.DeepClone()) };
        foreach (var (k, v) in Versions) root[k] = v?.DeepClone();
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Every preset in a HandBrake export or presets.json. Folders are flattened, since HandBrake
    /// nests presets in ChildrenArray under folder entries.
    /// </summary>
    public static List<HandBrakePreset> ReadFile(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("File is too large to be a HandBrake preset file.");
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not valid JSON: {ex.Message}", ex);
        }
        if (root is not JsonObject obj || obj["PresetList"] is not JsonArray list)
            throw new InvalidDataException("Not a HandBrake preset file (no PresetList).");

        var versions = new JsonObject();
        foreach (var k in (string[])["VersionMajor", "VersionMinor", "VersionMicro"])
            versions[k] = obj[k]?.DeepClone() ?? 0;

        var presets = new List<HandBrakePreset>();
        void Walk(JsonArray items, int depth)
        {
            if (depth > 16) return;
            foreach (var p in items.OfType<JsonObject>())
            {
                if (p["Folder"] is JsonValue f && f.TryGetValue<bool>(out var isFolder) && isFolder)
                {
                    if (p["ChildrenArray"] is JsonArray children) Walk(children, depth + 1);
                }
                else
                {
                    presets.Add(new HandBrakePreset((JsonObject)p.DeepClone(), versions));
                }
            }
        }
        Walk(list, 0);
        if (presets.Count == 0) throw new InvalidDataException("The file contains no presets.");
        return presets;
    }
}

/// <summary>Saved presets, one HandBrake-format file each under %LOCALAPPDATA%\WinVideoTools\presets.</summary>
public sealed class PresetStore(string folder)
{
    public static PresetStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinVideoTools", "presets"));

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Saved presets sorted by name, plus a message per file that could not be read.</summary>
    public (List<HandBrakePreset> Presets, List<string> Errors) Load()
    {
        var presets = new List<HandBrakePreset>();
        var errors = new List<string>();
        if (!Directory.Exists(folder)) return (presets, errors);
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                presets.AddRange(HandBrakePreset.ReadFile(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        presets.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return (presets, errors);
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Saves the preset, replacing any saved preset with the same name.</summary>
    public void Save(HandBrakePreset preset)
    {
        Directory.CreateDirectory(folder);
        var path = PathFor(preset.Name);
        // Write then swap, so a crash mid-write cannot leave a truncated preset behind.
        var temp = path + ".tmp";
        File.WriteAllText(temp, preset.ToFileJson());
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string name) => File.Delete(PathFor(name));

    /// <summary>
    /// The file for a preset name. Names come from imported files, so strip anything that could
    /// escape the folder or name a device; two names that sanitize alike share a file.
    /// </summary>
    internal string PathFor(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length > 100) safe = safe[..100];
        if (safe.Length == 0 || safe.All(c => c == '.')) safe = "_";
        if (ReservedNames.Contains(safe.Split('.')[0])) safe = "_" + safe;

        var path = Path.GetFullPath(Path.Combine(folder, safe + ".json"));
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Preset name does not map to a file in the preset folder.");
        return path;
    }
}
