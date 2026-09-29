using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinVideoTools.VideoVerifier;

public sealed record CheckResult(bool IsValid, string Summary, string Details, bool HasMinorErrors = false);

/// <summary>
/// Validates a video by decoding every frame with ffmpeg. A file is invalid when ffmpeg fails,
/// logs any error, or decodes noticeably less than the duration the container header claims
/// (a truncated file often plays fine but ends early).
/// </summary>
public static partial class VideoChecker
{
    // Decoded length can trail the header duration slightly (stream padding, audio/video skew).
    private const double MinToleranceSeconds = 1.0;
    private const double ToleranceFraction = 0.02;
    private const int MaxLogLines = 200;

    [GeneratedRegex(@"\[(error|fatal|panic)\]\s*(.*)$")]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"\[warning\]")]
    private static partial Regex WarningLine();

    [GeneratedRegex(@"Duration: (\d+):(\d\d):(\d\d(?:\.\d+)?)")]
    private static partial Regex DurationLine();

    [GeneratedRegex(@"^packet_obj_size -?\d+ invalid$")]
    private static partial Regex AsfBadPacket();

    [GeneratedRegex(@"^Error or Bits overconsumption: (\d+) > (\d+)")]
    private static partial Regex BitsOverconsumption();

    // A frame that overran its data by at most this many bits decoded almost entirely correctly.
    private const int MaxMinorOverrunBits = 64;

    private const string MinorNote =
        "NOTE: This file has minor glitches that do not affect playback. A few ASF packet headers are "
        + "damaged (\"packet_obj_size ... invalid\"), so those packets are skipped, and the frames next to "
        + "them overrun their data by a few bits (\"Bits overconsumption\"), which can leave one frame "
        + "very slightly off. Players hide this. Converting the file produces a copy without these errors.";

    /// <summary>
    /// True for ffmpeg errors that mark tiny, invisible damage: a skipped ASF packet, or a frame that
    /// overran its data by only a few bits. Such files play normally.
    /// </summary>
    internal static bool IsMinorError(string message)
    {
        if (AsfBadPacket().IsMatch(message)) return true;
        return BitsOverconsumption().Match(message) is { Success: true } m
               && long.TryParse(m.Groups[1].Value, out var used)
               && long.TryParse(m.Groups[2].Value, out var available)
               && used - available <= MaxMinorOverrunBits;
    }

    /// <summary>Parses the "Duration: hh:mm:ss.ff" line ffmpeg prints for an input.</summary>
    internal static bool TryParseDuration(string line, out double seconds)
    {
        seconds = 0;
        if (DurationLine().Match(line) is not { Success: true } d) return false;
        seconds = int.Parse(d.Groups[1].Value) * 3600
                + int.Parse(d.Groups[2].Value) * 60
                + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
        return true;
    }

    public static async Task<CheckResult> CheckAsync(string ffmpegPath, string file, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // ArgumentList quotes each argument, so file names cannot inject options.
        // The file: prefix stops ffmpeg treating parts of the name as a protocol or device.
        foreach (var arg in new[]
        {
            "-hide_banner", "-nostdin", "-nostats",
            "-loglevel", "level+info",
            "-progress", "pipe:1",
            "-i", "file:" + file,
            "-f", "null", "-",
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        // Redirected pipes are synchronous handles, so a pending read ignores the token.
        // Killing ffmpeg closes the pipes, which unblocks the reads.
        using var killOnCancel = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        try
        {
            double duration = 0;
            var log = new List<string>();
            var errors = new List<string>();
            var minor = 0;

            // Stream info and errors arrive on stderr; the Duration line is printed before any progress.
            var stderrTask = Task.Run(async () =>
            {
                while (await proc.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    // The null muxer is our own throwaway output. Its complaints (such as repeated
                    // timestamps, common in VFR and screen recordings) say nothing about whether the
                    // file decodes, so log them without failing the file.
                    // Same for a dangling chapter reference in the MP4 header: only chapter markers
                    // are lost, the streams still decode.
                    if (line.StartsWith("[null @", StringComparison.Ordinal)
                        || line.EndsWith("[error] Referenced QT chapter track not found", StringComparison.Ordinal))
                    {
                        if (log.Count < MaxLogLines) log.Add(line);
                    }
                    else if (ErrorLine().Match(line) is { Success: true } err)
                    {
                        // ponytail: no cap on minor errors; a file with hundreds of them would pass. Add a count limit if that shows up.
                        if (IsMinorError(err.Groups[2].Value)) minor++;
                        else errors.Add(err.Groups[2].Value);
                        if (log.Count < MaxLogLines) log.Add(line);
                    }
                    else if (WarningLine().IsMatch(line))
                    {
                        if (log.Count < MaxLogLines) log.Add(line);
                    }
                    else if (duration == 0 && TryParseDuration(line, out var d))
                    {
                        duration = d;
                    }
                }
            }, ct);

            double decoded = 0;
            while (await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan("out_time_us=".Length), out var us) && us > 0)
                {
                    decoded = us / 1_000_000.0;
                    if (duration > 0) progress?.Report(Math.Min(1, decoded / duration));
                }
            }

            await stderrTask.ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            // A cancel kills ffmpeg, which ends the output early without an exception; never judge that output.
            ct.ThrowIfCancellationRequested();

            var problems = new List<string>();
            if (errors.Count > 0)
                problems.Add(errors.Count == 1 ? errors[0] : $"{errors[0]} (+{errors.Count - 1} more errors)");
            if (duration > 0 && decoded < duration - Math.Max(MinToleranceSeconds, duration * ToleranceFraction))
                problems.Add($"Decoded {decoded:0.0}s of {duration:0.0}s");
            if (proc.ExitCode != 0 && problems.Count == 0)
                problems.Add($"ffmpeg exited with code {proc.ExitCode}");

            if (minor > 0) log.InsertRange(0, [MinorNote, ""]);
            var details = string.Join(Environment.NewLine, log);
            var ok = $"OK, {TimeSpan.FromSeconds(Math.Round(decoded)):g}";
            if (minor > 0) ok += $", {minor} minor error{(minor == 1 ? "" : "s")}";
            return problems.Count == 0
                ? new CheckResult(true, ok, details, minor > 0)
                : new CheckResult(false, string.Join("; ", problems), details);
        }
        finally
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
    }
}
