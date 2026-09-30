using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using WinVideoTools.VideoVerifier;

namespace WinVideoTools.SimilarVideos;

/// <summary>
/// A cheap perceptual fingerprint: 64-bit difference hashes of short bursts of frames sampled evenly through the video.
/// Black bars are cropped first, so it survives re-encoding, resizing, container changes, letterboxing and
/// small trims. Does not detect clips cut from inside a longer video, mirrored or heavily cropped copies.
/// </summary>
public sealed partial record VideoFingerprint(double Duration, int Width, int Height, string Codec, ulong[][] Samples)
{
    public const int SampleCount = 10;
    // Each sample is a burst of frames spanning twice the allowed drift, so copies whose sample times drift
    // apart (durations differ slightly, or a few seconds were cut from the start) still compare the same moment.
    public const double DefaultMaxDrift = 1, MaxMaxDrift = 10;
    private const int BurstFps = 10;

    /// <summary>The drift, in seconds, the bursts were sized for. Fingerprints only compare well with the same setting.</summary>
    public double MaxDrift { get; init; }

    /// <summary>Video stream bit rate in kb/s as the container reports it; 0 when it does not (MKV, WebM).</summary>
    public int VideoBitrate { get; init; }

    // Bits (of 64) two frame hashes may differ by and still count as the same picture.
    private const int MaxFrameDistance = 10;
    // Share of the sparser video's frames that must find a match in the other video.
    private const double MinMatchFraction = 0.6;
    private const int MinMatches = 3;
    // Durations must be within this, so a short clip never matches the full video it came from.
    private const double DurationToleranceFraction = 0.1;
    private const double MinDurationToleranceSeconds = 2;
    // Frames flatter than this (black, fades, solid titles) carry no information and would match everything.
    private const int MinContrast = 24;

    private const int HashWidth = 9, HashHeight = 8;
    // Frames are grabbed at this square size so black bars can be cropped before hashing.
    internal const int GrabSize = 64;
    // An edge row or column counts as a letterbox or pillarbox bar when this share of it is darker than BorderLuma.
    // Below 1 so a watermark sitting inside a bar does not stop the crop.
    private const int BorderLuma = 32;
    private const double BorderDarkFraction = 0.75;

    [GeneratedRegex(@"Stream #\d+:\d+.*?: Video: (\w+).*?, (\d{2,5})x(\d{2,5})")]
    private static partial Regex VideoStreamLine();

    [GeneratedRegex(@", (\d+) kb/s")]
    private static partial Regex StreamBitrate();

    public static async Task<VideoFingerprint> ComputeAsync(string ffmpegPath, string file, double maxDrift, CancellationToken ct)
    {
        maxDrift = Math.Clamp(maxDrift, 0.1, MaxMaxDrift);
        // With no output ffmpeg just prints the input info and exits non-zero; that is expected.
        var (_, info) = await RunFfmpegAsync(ffmpegPath, ["-i", "file:" + file], ct).ConfigureAwait(false);
        double duration = 0;
        int width = 0, height = 0, bitrate = 0;
        var codec = "";
        foreach (var line in info.Split('\n'))
        {
            if (duration == 0 && VideoChecker.TryParseDuration(line, out var d)) duration = d;
            else if (width == 0 && VideoStreamLine().Match(line) is { Success: true } m)
            {
                codec = m.Groups[1].Value;
                width = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                height = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (StreamBitrate().Match(line) is { Success: true } b) int.TryParse(b.Groups[1].Value, CultureInfo.InvariantCulture, out bitrate);
            }
        }
        if (duration <= 0 || width == 0) throw new InvalidDataException("Could not read video duration or resolution.");

        const int frameSize = GrabSize * GrabSize;
        var samples = new List<ulong[]>(SampleCount);
        for (var i = 0; i < SampleCount; i++)
        {
            var start = Math.Max(0, duration * (i + 0.5) / SampleCount - maxDrift);
            // -ss before -i seeks by keyframe then decodes forward, so only a few seconds are read.
            var (frames, _) = await RunFfmpegAsync(ffmpegPath,
            [
                "-loglevel", "error", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-i", "file:" + file,
                "-map", "0:v:0", "-frames:v", ((int)Math.Ceiling(2 * maxDrift * BurstFps)).ToString(CultureInfo.InvariantCulture),
                "-vf", $"fps={BurstFps},scale={GrabSize}:{GrabSize}:flags=area,format=gray",
                "-f", "rawvideo", "pipe:1",
            ], ct).ConfigureAwait(false);
            var burst = Enumerable.Range(0, frames.Length / frameSize)
                .Select(f => DHash(CropAndShrink(frames.AsSpan(f * frameSize, frameSize))))
                .OfType<ulong>()
                .ToArray();
            // Kept even when empty (all flat frames), so sample i is the same moment in every fingerprint.
            samples.Add(burst);
        }
        return new VideoFingerprint(duration, width, height, codec, [.. samples]) { MaxDrift = maxDrift, VideoBitrate = bitrate };
    }

    /// <summary>A JPEG of the frame at <paramref name="at"/> seconds, scaled down to at most 960 pixels wide.</summary>
    public static async Task<byte[]> ScreenshotAsync(string ffmpegPath, string file, double at, CancellationToken ct)
    {
        var (jpeg, err) = await RunFfmpegAsync(ffmpegPath,
        [
            "-loglevel", "error", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", "file:" + file,
            "-map", "0:v:0", "-frames:v", "1", "-vf", @"scale=w=min(iw\,960):h=-2",
            "-c:v", "mjpeg", "-q:v", "3", "-f", "image2pipe", "pipe:1",
        ], ct).ConfigureAwait(false);
        return jpeg.Length > 0 ? jpeg : throw new InvalidDataException(err.Trim() is { Length: > 0 } e ? e : "No frame decoded.");
    }

    /// <summary>
    /// Crops dark bars off a <see cref="GrabSize"/> square grayscale frame and area-averages the rest to 9x8,
    /// so copies with different letterboxing, or with bars cropped off, hash the same picture.
    /// </summary>
    internal static byte[] CropAndShrink(ReadOnlySpan<byte> gray)
    {
        const int n = GrabSize;
        int top = 0, bottom = n, left = 0, right = n;
        while (top < bottom && IsDark(gray, top * n + left, 1, right - left)) top++;
        while (bottom > top && IsDark(gray, (bottom - 1) * n + left, 1, right - left)) bottom--;
        while (left < right && IsDark(gray, top * n + left, n, bottom - top)) left++;
        while (right > left && IsDark(gray, top * n + right - 1, n, bottom - top)) right--;

        var result = new byte[HashWidth * HashHeight];
        // Nothing left but darkness: an all-black result, which DHash rejects as flat.
        int w = right - left, h = bottom - top;
        if (w < HashWidth || h < HashHeight) return result;
        for (var y = 0; y < HashHeight; y++)
        {
            for (var x = 0; x < HashWidth; x++)
            {
                int y0 = top + y * h / HashHeight, y1 = top + (y + 1) * h / HashHeight;
                int x0 = left + x * w / HashWidth, x1 = left + (x + 1) * w / HashWidth;
                var sum = 0;
                for (var yy = y0; yy < y1; yy++)
                    for (var xx = x0; xx < x1; xx++)
                        sum += gray[yy * n + xx];
                result[y * HashWidth + x] = (byte)(sum / ((y1 - y0) * (x1 - x0)));
            }
        }
        return result;
    }

    private static bool IsDark(ReadOnlySpan<byte> gray, int start, int step, int count)
    {
        var dark = 0;
        for (var i = 0; i < count; i++)
            if (gray[start + i * step] < BorderLuma) dark++;
        return dark >= count * BorderDarkFraction;
    }

    /// <summary>Difference hash of a 9x8 grayscale frame: one bit per horizontally adjacent pixel pair. Null for flat frames.</summary>
    internal static ulong? DHash(ReadOnlySpan<byte> gray)
    {
        byte min = 255, max = 0;
        foreach (var p in gray)
        {
            min = Math.Min(min, p);
            max = Math.Max(max, p);
        }
        if (max - min < MinContrast) return null;

        ulong hash = 0;
        for (var y = 0; y < HashHeight; y++)
            for (var x = 0; x < HashWidth - 1; x++)
                hash = hash << 1 | (gray[y * HashWidth + x] < gray[y * HashWidth + x + 1] ? 1UL : 0);
        return hash;
    }

    private static double DurationTolerance(double longer) => Math.Max(MinDurationToleranceSeconds, longer * DurationToleranceFraction);

    public bool IsSimilarTo(VideoFingerprint other)
    {
        if (Math.Abs(Duration - other.Duration) > DurationTolerance(Math.Max(Duration, other.Duration))) return false;

        // Only samples where both bursts hold usable frames count.
        var usable = Enumerable.Range(0, Math.Min(Samples.Length, other.Samples.Length))
            .Where(i => Samples[i].Length > 0 && other.Samples[i].Length > 0)
            .ToList();
        if (usable.Count < MinMatches) return false;
        // A sample matches when any frame of its burst matches any frame of the other video's burst at the same
        // position; the bursts already span the drift. Matching against any burst, in any order, made unrelated
        // videos from the same studio (same sets, lighting, framing) match almost always.
        var matches = usable.Count(i => Samples[i].Any(h => other.Samples[i].Any(o => BitOperations.PopCount(h ^ o) <= MaxFrameDistance)));
        return matches >= MinMatches && matches >= usable.Count * MinMatchFraction;
    }

    /// <summary>Indexes of similar fingerprints grouped transitively; files with no match are left out.</summary>
    public static List<List<int>> Group(IReadOnlyList<VideoFingerprint> prints)
    {
        var parent = Enumerable.Range(0, prints.Count).ToArray();
        int Root(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        // Sorted by duration, each file only needs comparing with the few after it within tolerance.
        var order = Enumerable.Range(0, prints.Count).OrderBy(i => prints[i].Duration).ToArray();
        for (var a = 0; a < order.Length; a++)
        {
            for (var b = a + 1; b < order.Length; b++)
            {
                var (pa, pb) = (prints[order[a]], prints[order[b]]);
                if (pb.Duration - pa.Duration > DurationTolerance(pb.Duration)) break;
                if (pa.IsSimilarTo(pb)) parent[Root(order[a])] = Root(order[b]);
            }
        }

        return Enumerable.Range(0, prints.Count)
            .GroupBy(Root)
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();
    }

    private static async Task<(byte[] Stdout, string Stderr)> RunFfmpegAsync(string ffmpegPath, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // ArgumentList quotes each argument, and callers prefix the path with file:, so names cannot inject options.
        foreach (var arg in (string[])["-hide_banner", "-nostdin", .. args]) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        // Redirected pipes are synchronous handles that ignore tokens; killing ffmpeg is what unblocks the reads.
        using var killOnCancel = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        try
        {
            var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            using var stdout = new MemoryStream();
            await proc.StandardOutput.BaseStream.CopyToAsync(stdout, CancellationToken.None).ConfigureAwait(false);
            var err = await stderr.ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            // A cancel kills ffmpeg, which ends the output early without an exception; never return that as a result.
            ct.ThrowIfCancellationRequested();
            return (stdout.ToArray(), err);
        }
        finally
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
    }
}
