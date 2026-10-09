using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinVideoTools.Converter;

/// <summary>
/// Running totals for every file the converter has finished, kept apart from the history so clearing or importing
/// the history leaves them alone. Used from the UI thread only; writes take a JSON copy.
/// </summary>
public sealed class ConvertStats
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>When counting started: the first run with stats, or the last reset. Required, so other JSON is not read as stats.</summary>
    [JsonRequired]
    public DateTime SinceUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastConvertedUtc { get; set; }

    /// <summary>Conversions whose output was kept, and the sizes of their sources and outputs.</summary>
    public int Converted { get; set; }
    public long InputBytes { get; set; }
    public long OutputBytes { get; set; }

    /// <summary>Sources deleted after a smaller output, and the disk space that gave back.</summary>
    public int OriginalsDeleted { get; set; }
    public long FreedBytes { get; set; }

    /// <summary>Outputs deleted for being larger than their source.</summary>
    public int Discarded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    /// <summary>Time spent encoding, including runs that failed or were discarded.</summary>
    public TimeSpan EncodeTime { get; set; }

    public string? BiggestSavingName { get; set; }
    public long BiggestSavingBytes { get; set; }

    public long SavedBytes => InputBytes - OutputBytes;

    /// <summary>Counts one finished file. Cancelled and unfinished files are not counted.</summary>
    /// <param name="outputKept">An output still exists, possibly renamed to the source's name.</param>
    public void Add(ConvertStatus status, string name, long inputSize, long? outputSize, bool outputKept, bool originalDeleted, TimeSpan elapsed)
    {
        switch (status)
        {
            case ConvertStatus.Skipped:
                Skipped++;
                return;
            case ConvertStatus.Failed:
                Failed++;
                break;
            case ConvertStatus.Done when outputKept && outputSize is { } output:
                Converted++;
                InputBytes += inputSize;
                OutputBytes += output;
                LastConvertedUtc = DateTime.UtcNow;
                if (inputSize - output > BiggestSavingBytes)
                {
                    BiggestSavingBytes = inputSize - output;
                    BiggestSavingName = name;
                }
                if (originalDeleted)
                {
                    OriginalsDeleted++;
                    FreedBytes += Math.Max(0, inputSize - output);
                }
                break;
            case ConvertStatus.Done:
                Discarded++;
                break;
            default:
                return;
        }
        if (elapsed > TimeSpan.Zero) EncodeTime += elapsed;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <exception cref="InvalidDataException">The file is not a stats file.</exception>
    public static ConvertStats Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ConvertStats>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Not a convert stats file.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Not a convert stats file.", ex);
        }
    }
}
