using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinVideoTools.VideoVerifier;

namespace WinVideoTools.Converter;

// Width and Height are 0 when unknown, and for non-video streams.
public sealed record SourceStream(int Index, string Kind, string Language, string Codec, bool AttachedPic, int Width = 0, int Height = 0);

public sealed record SourceInfo(double Duration, List<SourceStream> Streams);

// Notes are choices made for this file, such as a fallback audio track or a skipped subtitle.
public sealed record ConvertResult(bool Success, string Summary, string Log, IReadOnlyList<string> Notes);

/// <summary>
/// Translates a HandBrake preset into an ffmpeg command line and runs it. HandBrake has its own
/// engine, so this is a mapping, not an exact match: settings with no ffmpeg equivalent are
/// reported by <see cref="Unsupported"/> and skipped.
/// </summary>
public static partial class PresetConverter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int MaxLogLines = 200;

    // PROBE

    [GeneratedRegex(@"^\s*Stream #0:(\d+)(?:\[0x[0-9a-fA-F]+\])?(?:\(([^)]*)\))?: (Video|Audio|Subtitle): (\w+)")]
    private static partial Regex StreamLine();

    // "1920x1080" after the codec. At least two digits each side, so codec tags like "0x31637661" never match.
    [GeneratedRegex(@"\b(\d{2,5})x(\d{2,5})\b")]
    private static partial Regex FrameSize();

    /// <summary>
    /// Whether the skip-HEVC option applies. "1080p or smaller" means the shorter side is at most 1080,
    /// so portrait and ultrawide 1080p count. An unknown size is never skipped under that limit.
    /// </summary>
    internal static bool ShouldSkipHevc(SourceInfo src, bool onlyUpTo1080p)
    {
        if (src.Streams.FirstOrDefault(s => s.Kind == "Video" && !s.AttachedPic) is not { Codec: "hevc" } v) return false;
        return !onlyUpTo1080p || (v.Width > 0 && v.Height > 0 && Math.Min(v.Width, v.Height) <= 1080);
    }

    /// <summary>Reads the stream list and duration ffmpeg prints for an input.</summary>
    internal static SourceInfo ParseInfo(string stderr)
    {
        double duration = 0;
        var streams = new List<SourceStream>();
        foreach (var line in stderr.Split('\n'))
        {
            if (duration == 0 && VideoChecker.TryParseDuration(line, out var d)) duration = d;
            if (StreamLine().Match(line) is { Success: true } m)
            {
                var size = m.Groups[3].Value == "Video" ? FrameSize().Match(line, m.Length) : Match.Empty;
                streams.Add(new SourceStream(int.Parse(m.Groups[1].Value, Inv), m.Groups[3].Value,
                    m.Groups[2].Value.ToLowerInvariant(), m.Groups[4].Value, line.Contains("(attached pic)", StringComparison.Ordinal),
                    size.Success ? int.Parse(size.Groups[1].Value, Inv) : 0, size.Success ? int.Parse(size.Groups[2].Value, Inv) : 0));
            }
        }
        return new SourceInfo(duration, streams);
    }

    public static async Task<SourceInfo> ProbeAsync(string ffmpegPath, string file, CancellationToken ct)
    {
        var psi = NewPsi(ffmpegPath, ["-hide_banner", "-nostdin", "-i", "file:" + file]);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        using var kill = KillOnCancel(proc, ct);
        // With no output ffmpeg prints the input info and exits non-zero; that is expected.
        var stderr = await proc.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return ParseInfo(stderr);
    }

    // CONTAINER

    public static (string Extension, string Format) Container(HandBrakePreset p) => p.Str("FileFormat") switch
    {
        "av_mp4" => (".mp4", "mp4"),
        "av_webm" => (".webm", "webm"),
        _ => (".mkv", "matroska"),
    };

    /// <summary>Output path for a source: same name with the preset's extension, never an existing file.</summary>
    public static string OutputPathFor(string source, string? outputFolder, HandBrakePreset p)
    {
        var folder = string.IsNullOrWhiteSpace(outputFolder) ? Path.GetDirectoryName(source)! : outputFolder;
        var name = Path.GetFileNameWithoutExtension(source);
        var ext = Container(p).Extension;
        var dest = Path.Combine(folder, name + ext);
        for (var n = 2; File.Exists(dest) || Directory.Exists(dest); n++)
            dest = Path.Combine(folder, $"{name} ({n}){ext}");
        return dest;
    }

    // VIDEO ENCODERS

    private enum Family { X264, X265, Nvenc, Qsv, Amf, Svt, Vpx, Other }

    private sealed record VideoEnc(string Name, Family Family, string? PixFmt = null);

    private static VideoEnc? VideoEncoder(string hb) => hb.ToLowerInvariant() switch
    {
        "x264" => new("libx264", Family.X264),
        "x264_10bit" => new("libx264", Family.X264, "yuv420p10le"),
        "x265" => new("libx265", Family.X265),
        "x265_10bit" => new("libx265", Family.X265, "yuv420p10le"),
        "x265_12bit" => new("libx265", Family.X265, "yuv420p12le"),
        "nvenc_h264" => new("h264_nvenc", Family.Nvenc),
        "nvenc_h265" => new("hevc_nvenc", Family.Nvenc),
        "nvenc_h265_10bit" => new("hevc_nvenc", Family.Nvenc, "p010le"),
        "nvenc_av1" => new("av1_nvenc", Family.Nvenc),
        "nvenc_av1_10bit" => new("av1_nvenc", Family.Nvenc, "p010le"),
        "qsv_h264" => new("h264_qsv", Family.Qsv),
        "qsv_h265" => new("hevc_qsv", Family.Qsv),
        "qsv_h265_10bit" => new("hevc_qsv", Family.Qsv, "p010le"),
        "qsv_av1" => new("av1_qsv", Family.Qsv),
        "qsv_av1_10bit" => new("av1_qsv", Family.Qsv, "p010le"),
        "vce_h264" => new("h264_amf", Family.Amf),
        "vce_h265" => new("hevc_amf", Family.Amf),
        "vce_h265_10bit" => new("hevc_amf", Family.Amf, "p010le"),
        "vce_av1" => new("av1_amf", Family.Amf),
        "vce_av1_10bit" => new("av1_amf", Family.Amf, "p010le"),
        "svt_av1" => new("libsvtav1", Family.Svt),
        "svt_av1_10bit" => new("libsvtav1", Family.Svt, "yuv420p10le"),
        "vp8" => new("libvpx", Family.Vpx),
        "vp9" => new("libvpx-vp9", Family.Vpx),
        "vp9_10bit" => new("libvpx-vp9", Family.Vpx, "yuv420p10le"),
        "mpeg4" => new("mpeg4", Family.Other),
        "mpeg2" => new("mpeg2video", Family.Other),
        "theora" => new("libtheora", Family.Other),
        "ffv1" => new("ffv1", Family.Other),
        _ => null,
    };

    private static readonly HashSet<string> X26xPresets =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo"];

    private static readonly HashSet<string> X26xTunes =
        ["film", "animation", "grain", "stillimage", "psnr", "ssim", "fastdecode", "zerolatency"];

    private static readonly HashSet<string> NvencTunes = ["hq", "uhq", "ll", "ull", "lossless"];

    // x264/x265 options that read or write files. Imported presets are untrusted, so these never reach the encoder.
    private static readonly HashSet<string> FileOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "qpfile", "dump-yuv", "stats", "cqmfile", "tcfile-in", "tcfile-out", "csv", "analysis-save", "analysis-load",
        "analysis-reuse-file", "zonefile", "lambda-file", "scaling-list", "dolby-vision-rpu", "recon", "dhdr10-info",
    };

    /// <summary>Splits HandBrake's "key=value:key=value" extra options, dropping any that touch files.</summary>
    internal static (string Clean, List<string> Dropped) SanitizeEncoderOptions(string extra)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        foreach (var opt in extra.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = opt.Split('=', 2)[0];
            if (FileOptions.Contains(key) || opt.Contains('/') || opt.Contains('\\')) dropped.Add(opt);
            else kept.Add(opt);
        }
        return (string.Join(':', kept), dropped);
    }

    private static string N(double v) => v.ToString("0.###", Inv);

    private static bool IsOff(string v) => v is "" or "off" or "none";

    // UNSUPPORTED

    /// <summary>Preset settings that will be ignored or only approximated. Shown before converting.</summary>
    public static List<string> Unsupported(HandBrakePreset p)
    {
        var notes = new List<string>();
        var enc = VideoEncoder(p.Str("VideoEncoder"));
        if (enc is null) notes.Add($"Video encoder \"{p.Str("VideoEncoder")}\" is not supported; x264 is used instead.");
        if (p.Str("FileFormat") is not ("av_mkv" or "av_mp4" or "av_webm" or ""))
            notes.Add($"Container \"{p.Str("FileFormat")}\" is not supported; MKV is used instead.");
        if (p.Num("VideoQualityType", 2) == 1 && p.Bool("VideoMultiPass")) notes.Add("Multi-pass encoding is not supported; one pass is used.");
        if (p.Num("VideoQualityType", 2) is not (1 or 2)) notes.Add("Target-size encoding is not supported; constant quality is used.");
        if (p.Str("VideoOptionExtra").Trim() is { Length: > 0 } extra)
        {
            if (enc?.Family is Family.X264 or Family.X265)
            {
                var (_, dropped) = SanitizeEncoderOptions(extra);
                if (dropped.Count > 0) notes.Add($"Encoder options that access files were removed: {string.Join(", ", dropped)}.");
            }
            else
            {
                notes.Add("Advanced encoder options are only applied for x264 and x265.");
            }
        }
        if (IsAutoCrop(p)) notes.Add("Automatic crop is not applied; the full frame is kept.");
        if (p.Str("PictureDenoiseFilter") is var dn && !IsOff(dn) && dn is not ("hqdn3d" or "nlmeans"))
            notes.Add($"Denoise filter \"{dn}\" is not supported.");
        if (p.Str("PictureDenoiseFilter") == "nlmeans") notes.Add("NLMeans denoise is approximated; strength will differ from HandBrake.");
        if (!IsOff(p.Str("PictureDenoiseFilter")) && p.Str("PictureDenoisePreset") == "custom") notes.Add("Custom denoise settings are not supported; medium strength is used.");
        foreach (var (key, label) in (ReadOnlySpan<(string, string)>)[
            ("PictureSharpenFilter", "Sharpen"), ("PictureDeblockPreset", "Deblock"), ("PictureChromaSmoothPreset", "Chroma smooth"),
            ("PictureColorspacePreset", "Colorspace"), ("PicturePadMode", "Padding")])
        {
            if (!IsOff(p.Str(key))) notes.Add($"{label} is not supported.");
        }
        if (p.Str("PictureRotate") is { Length: > 0 } rot && rot is not ("angle=0:hflip=0" or "0")) notes.Add("Rotate and flip are not supported.");
        if (p.Str("PictureDetelecine") is var dt && !IsOff(dt)) notes.Add("Detelecine is approximated with ffmpeg's pullup filter.");
        if (p.Bool("SubtitleAddForeignAudioSearch")) notes.Add("Foreign audio search is not supported.");
        if (!IsOff(p.Str("SubtitleBurnBehavior", "none"))) notes.Add("Subtitle burn-in is not supported; subtitles are added as tracks where the container allows.");
        if (p.Bool("SubtitleAddCC")) notes.Add("Closed captions are not added.");
        if (p.ObjList("AudioList").Any(a => a["AudioTrackDRCSlider"] is JsonValue v && v.TryGetValue<double>(out var drc) && drc != 0))
            notes.Add("Audio dynamic range compression is not supported.");
        if (p.ObjList("AudioList").Any(a => a["AudioTrackQualityEnable"] is JsonValue v && v.TryGetValue<bool>(out var q) && q))
            notes.Add("Audio quality mode is not supported; the bitrate is used instead.");
        return notes;
    }

    // HandBrake 1.8+: 0 automatic, 1 conservative, 2 none, 3 custom. Older presets use PictureAutoCrop.
    private static bool IsAutoCrop(HandBrakePreset p) =>
        p.Json.ContainsKey("PictureCropMode") ? p.Num("PictureCropMode") is 0 or 1 : p.Bool("PictureAutoCrop", true);

    // BUILD

    /// <summary>
    /// The full ffmpeg argument list for one file, plus notes about choices made for this file
    /// (fallback tracks, subtitles the container cannot hold).
    /// </summary>
    public static (List<string> Args, List<string> Notes) BuildArgs(HandBrakePreset p, SourceInfo src, string input, string output)
    {
        var notes = new List<string>();
        var video = src.Streams.FirstOrDefault(s => s.Kind == "Video" && !s.AttachedPic)
                    ?? throw new InvalidDataException("The file has no video stream.");
        var (_, format) = Container(p);

        List<string> a =
        [
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "level+warning", "-progress", "pipe:1", "-n",
            "-i", "file:" + input,
            "-map", $"0:{video.Index}",
        ];
        AddVideo(p, a);
        AddAudio(p, src, a, notes);
        AddSubtitles(p, src, format, a, notes);

        if (!p.Bool("ChapterMarkers", true)) a.AddRange(["-map_chapters", "-1"]);
        if (!p.Bool("MetadataPassthru", true)) a.AddRange(["-map_metadata", "-1"]);
        if (format == "mp4" && p.Bool("Optimize")) a.AddRange(["-movflags", "+faststart"]);
        a.AddRange(["-f", format, "file:" + output]);
        return (a, notes);
    }

    private static void AddVideo(HandBrakePreset p, List<string> a)
    {
        var enc = VideoEncoder(p.Str("VideoEncoder")) ?? new VideoEnc("libx264", Family.X264);
        a.AddRange(["-c:v", enc.Name]);

        // RATE CONTROL
        var q = p.Num("VideoQualitySlider", 22);
        if (p.Num("VideoQualityType", 2) == 1 && p.Num("VideoAvgBitrate") > 0)
        {
            a.AddRange(["-b:v", N(p.Num("VideoAvgBitrate")) + "k"]);
        }
        else
        {
            var qi = ((int)Math.Round(q)).ToString(Inv);
            switch (enc.Family)
            {
                case Family.X264 or Family.X265: a.AddRange(["-crf", N(q)]); break;
                case Family.Svt: a.AddRange(["-crf", qi]); break;
                case Family.Nvenc: a.AddRange(["-rc", "vbr", "-cq", N(q), "-b:v", "0"]); break;
                case Family.Qsv: a.AddRange(["-global_quality", qi]); break;
                case Family.Amf: a.AddRange(["-rc", "cqp", "-qp_i", qi, "-qp_p", qi]); break;
                case Family.Vpx: a.AddRange(["-crf", qi, "-b:v", "0"]); break;
                default: a.AddRange(["-q:v", N(q)]); break;
            }
        }

        // PRESET AND TUNE
        var preset = p.Str("VideoPreset").ToLowerInvariant();
        string? ff = enc.Family switch
        {
            Family.X264 or Family.X265 when X26xPresets.Contains(preset) => preset,
            Family.Nvenc => preset switch
            {
                "fastest" => "p1", "faster" => "p2", "fast" => "p3", "medium" => "p4",
                "slow" => "p5", "slower" => "p6", "slowest" => "p7", _ => null,
            },
            Family.Svt when int.TryParse(preset, Inv, out var n) && n is >= -1 and <= 13 => n.ToString(Inv),
            Family.Qsv => preset switch { "speed" => "veryfast", "balanced" => "medium", "quality" => "veryslow", _ => null },
            _ => null,
        };
        if (ff is not null) a.AddRange(["-preset", ff]);
        if (enc.Family == Family.Amf && preset is "speed" or "balanced" or "quality") a.AddRange(["-quality", preset]);

        var tunes = p.Str("VideoTune").ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allowed = enc.Family switch { Family.X264 or Family.X265 => X26xTunes, Family.Nvenc => NvencTunes, _ => [] };
        if (tunes.Length > 0 && tunes.All(allowed.Contains))
            a.AddRange(["-tune", enc.Family == Family.Nvenc ? tunes[0] : string.Join(',', tunes)]);

        if (p.Str("VideoProfile").ToLowerInvariant() is var prof && prof is not ("" or "auto") && ProfileLike().IsMatch(prof))
            a.AddRange(["-profile:v", prof]);
        if (p.Str("VideoLevel") is var lvl && lvl is not ("" or "auto") && LevelLike().IsMatch(lvl))
            a.AddRange(["-level", lvl]);
        if (enc.Family is Family.X264 or Family.X265 && SanitizeEncoderOptions(p.Str("VideoOptionExtra")).Clean is { Length: > 0 } extra)
            a.AddRange([enc.Family == Family.X264 ? "-x264-params" : "-x265-params", extra]);
        if (enc.PixFmt is not null) a.AddRange(["-pix_fmt", enc.PixFmt]);

        // FRAME RATE
        var mode = p.Str("VideoFramerateMode", "vfr");
        var rate = FrameRate(p.Str("VideoFramerate", "auto"));
        if (rate is null) a.AddRange(["-fps_mode", mode == "cfr" ? "cfr" : "passthrough"]);
        else if (mode == "cfr") a.AddRange(["-r", rate, "-fps_mode", "cfr"]);
        else a.AddRange(["-fpsmax", rate]);

        if (VideoFilters(p) is { Count: > 0 } filters) a.AddRange(["-vf", string.Join(',', filters)]);
    }

    [GeneratedRegex(@"^[a-z0-9-]{1,24}$")]
    private static partial Regex ProfileLike();

    [GeneratedRegex(@"^\d(\.\d)?$")]
    private static partial Regex LevelLike();

    // NTSC rates are stored rounded; ffmpeg needs the exact ratio.
    private static string? FrameRate(string hb)
    {
        if (!double.TryParse(hb, Inv, out var r) || r is < 1 or > 1000) return null;
        return Math.Round(r, 2) switch
        {
            23.98 => "24000/1001", 29.97 => "30000/1001", 59.94 => "60000/1001", 119.88 => "120000/1001",
            _ => N(r),
        };
    }

    /// <summary>
    /// The -vf chain. Every value is a parsed number or a name from a fixed list, never preset text,
    /// so a crafted preset cannot add filters (such as movie=, which reads files).
    /// </summary>
    private static List<string> VideoFilters(HandBrakePreset p)
    {
        var f = new List<string>();

        if (!IsOff(p.Str("PictureDetelecine"))) f.Add("pullup");

        var deint = p.Str("PictureDeinterlaceFilter");
        if (deint is "decomb" or "yadif" or "bwdif")
        {
            var sendField = p.Str("PictureDeinterlacePreset") is "bob" or "eedi2bob";
            // With comb detection off HandBrake deinterlaces every frame; otherwise only interlaced ones.
            var all = p.Str("PictureCombDetectPreset") == "off" && deint != "decomb";
            f.Add($"{(deint == "yadif" ? "yadif" : "bwdif")}=mode={(sendField ? "send_field" : "send_frame")}:deint={(all ? "all" : "interlaced")}");
        }

        var strength = p.Str("PictureDenoisePreset", "medium");
        switch (p.Str("PictureDenoiseFilter"))
        {
            case "hqdn3d":
                f.Add("hqdn3d=" + strength switch
                {
                    "ultralight" => "1:0.7:1:2", "light" => "2:1:2:3", "strong" => "7:7:5:5", _ => "3:2:2:3",
                });
                break;
            case "nlmeans":
                f.Add("nlmeans=s=" + strength switch { "ultralight" => "1", "light" => "2", "strong" => "8", _ => "4" });
                break;
        }

        if (!IsAutoCrop(p))
        {
            int C(string k) => (int)Math.Clamp(p.Num(k), 0, 16384) & ~1;
            var (t, b, l, r) = (C("PictureTopCrop"), C("PictureBottomCrop"), C("PictureLeftCrop"), C("PictureRightCrop"));
            if (t + b + l + r > 0) f.Add($"crop=iw-{l + r}:ih-{t + b}:{l}:{t}");
        }

        var w = (int)Math.Clamp(p.Num("PictureWidth"), 0, 16384);
        var h = (int)Math.Clamp(p.Num("PictureHeight"), 0, 16384);
        if (w > 0 || h > 0)
        {
            if (p.Bool("PictureKeepRatio", true) || w == 0 || h == 0)
            {
                // Fit inside the box, keeping aspect ratio; without upscaling, the box never exceeds the source.
                var bw = w > 0 ? w : 16384;
                var bh = h > 0 ? h : 16384;
                var up = p.Bool("PictureAllowUpscaling");
                f.Add($"scale=w={(up ? bw : $"'min({bw},iw)'")}:h={(up ? bh : $"'min({bh},ih)'")}:force_original_aspect_ratio=decrease:force_divisible_by=2");
            }
            else
            {
                f.Add($"scale={w & ~1}:{h & ~1},setsar=1");
            }
        }

        if (p.Bool("VideoGrayScale")) f.Add("hue=s=0");
        return f;
    }

    // AUDIO

    // ISO 639-2 bibliographic and terminology codes name the same language; files and presets mix them.
    private static readonly Dictionary<string, string> LangAlias = new()
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho", ["cze"] = "ces",
        ["dut"] = "nld", ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu", ["gre"] = "ell", ["ice"] = "isl",
        ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa", ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk",
        ["tib"] = "bod", ["wel"] = "cym",
    };

    private static string Lang(string code) => LangAlias.TryGetValue(code.ToLowerInvariant(), out var t) ? t : code.ToLowerInvariant();

    /// <summary>HandBrake's track selection: "first" takes the first track in each wanted language, "all" every match.</summary>
    internal static List<SourceStream> SelectTracks(IEnumerable<SourceStream> tracks, string behavior, List<string> languages)
    {
        var list = tracks.ToList();
        if (behavior == "none" || list.Count == 0) return [];
        var langs = languages.Select(Lang).ToList();
        if (langs.Count == 0 || langs.Contains("any")) return behavior == "all" ? list : [list[0]];

        if (behavior == "all") return list.Where(t => langs.Contains(Lang(t.Language))).ToList();
        return langs.Select(l => list.FirstOrDefault(t => Lang(t.Language) == l)).OfType<SourceStream>().Distinct().ToList();
    }

    private sealed record AudioEnc(string Name, string[] Extra, bool Lossless = false);

    private static AudioEnc? AudioEncoder(string hb) => hb switch
    {
        "av_aac" or "ca_aac" or "ca_haac" or "fdk_aac" or "fdk_haac" => new("aac", []),
        "mf_aac" => new("aac_mf", []),
        "ac3" or "mf_ac3" => new("ac3", []),
        "eac3" => new("eac3", []),
        "truehd" => new("truehd", ["-strict", "-2"], Lossless: true),
        "mp2" => new("mp2", []),
        "mp3" => new("libmp3lame", []),
        "vorbis" => new("libvorbis", []),
        "opus" => new("libopus", []),
        "flac16" or "flac" => new("flac", ["-sample_fmt", "s16"], Lossless: true),
        "flac24" => new("flac", ["-sample_fmt", "s32"], Lossless: true),
        "alac" or "alac16" or "alac24" => new("alac", [], Lossless: true),
        _ => null,
    };

    // HandBrake passthru names that differ from ffmpeg codec names.
    private static string CopyCodec(string hb) => hb switch { "dtshd" => "dts", _ => hb };

    private static void AddAudio(HandBrakePreset p, SourceInfo src, List<string> a, List<string> notes)
    {
        var entries = p.ObjList("AudioList");
        var available = src.Streams.Where(s => s.Kind == "Audio").ToList();
        var behavior = p.Str("AudioTrackSelectionBehavior", "first");
        var tracks = SelectTracks(available, behavior, p.StrList("AudioLanguageList"));
        // HandBrake falls back to the first track rather than dropping audio when no language matches.
        if (tracks.Count == 0 && behavior != "none" && available.Count > 0)
        {
            tracks = [available[0]];
            notes.Add("No audio track matched the preset's languages; the first track is used.");
        }
        if (entries.Count == 0 || tracks.Count == 0)
        {
            a.Add("-an");
            return;
        }

        var copyMask = p.StrList("AudioCopyMask").Select(m => CopyCodec(m.Replace("copy:", ""))).ToHashSet();
        var fallback = p.Str("AudioEncoderFallback", "av_aac");
        var k = 0;
        for (var t = 0; t < tracks.Count; t++)
        {
            // In secondary mode only the first track gets every encoder; the rest get the first one.
            var use = t > 0 && p.Bool("AudioSecondaryEncoderMode", true) ? entries.Take(1) : entries;
            foreach (var e in use)
            {
                var track = tracks[t];
                var hb = e["AudioEncoder"] is JsonValue ev && ev.TryGetValue<string>(out var s) ? s : "av_aac";
                if (hb.StartsWith("copy", StringComparison.Ordinal))
                {
                    var mask = hb == "copy" ? copyMask : [CopyCodec(hb.Replace("copy:", ""))];
                    if (mask.Contains(track.Codec))
                    {
                        a.AddRange(["-map", $"0:{track.Index}", $"-c:a:{k++}", "copy"]);
                        continue;
                    }
                    hb = fallback;
                }
                if (AudioEncoder(hb) is not { } enc)
                {
                    notes.Add($"Audio track {track.Index} skipped: encoder \"{hb}\" is not supported.");
                    continue;
                }

                a.AddRange(["-map", $"0:{track.Index}", $"-c:a:{k}", enc.Name]);
                foreach (var x in enc.Extra) a.Add(x.StartsWith('-') && x != "-2" ? $"{x}:a:{k}" : x);
                double Num(string key) => e[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : 0;

                if (!enc.Lossless && Num("AudioBitrate") is var br and > 0 and <= 10000) a.AddRange([$"-b:a:{k}", N(br) + "k"]);
                var mix = e["AudioMixdown"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : "";
                if (mix switch { "mono" or "left_only" or "right_only" => 1, "stereo" or "dpl1" or "dpl2" => 2, "5point1" => 6, "6point1" => 7, "7point1" or "5_2_lfe" => 8, _ => 0 } is var ch and > 0)
                    a.AddRange([$"-ac:a:{k}", ch.ToString(Inv)]);
                var sr = e["AudioSamplerate"] is JsonValue sv && sv.TryGetValue<string>(out var srs) && double.TryParse(srs, Inv, out var srd) ? srd : Num("AudioSamplerate");
                if (sr > 0 && sr < 1000) sr *= 1000;
                if (sr is >= 8000 and <= 192000) a.AddRange([$"-ar:a:{k}", ((int)sr).ToString(Inv)]);
                if (Num("AudioTrackGainSlider") is var gain && gain != 0 && Math.Abs(gain) <= 60) a.AddRange([$"-filter:a:{k}", $"volume={N(gain)}dB"]);
                k++;
            }
        }
        if (k == 0) a.Add("-an");
    }

    // SUBTITLES

    private static readonly HashSet<string> ImageSubs = ["hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub"];

    private static void AddSubtitles(HandBrakePreset p, SourceInfo src, string format, List<string> a, List<string> notes)
    {
        var tracks = SelectTracks(src.Streams.Where(s => s.Kind == "Subtitle"),
            p.Str("SubtitleTrackSelectionBehavior", "none"), p.StrList("SubtitleLanguageList"));
        var k = 0;
        foreach (var t in tracks)
        {
            var codec = format switch
            {
                // MKV holds every subtitle codec except MP4's own text format.
                "matroska" => t.Codec == "mov_text" ? "srt" : "copy",
                _ when ImageSubs.Contains(t.Codec) => null,
                "mp4" => "mov_text",
                _ => "webvtt",
            };
            if (codec is null)
            {
                notes.Add($"Subtitle track {t.Index} ({t.Codec}) skipped: {format.ToUpperInvariant()} cannot hold image subtitles.");
                continue;
            }
            a.AddRange(["-map", $"0:{t.Index}", $"-c:s:{k++}", codec]);
        }
        if (k == 0) a.Add("-sn");
    }

    // RUN

    public static async Task<ConvertResult> ConvertAsync(string ffmpegPath, HandBrakePreset preset, string input, string output,
        IProgress<double>? progress, Action<string>? onCommand, CancellationToken ct)
    {
        var src = await ProbeAsync(ffmpegPath, input, ct).ConfigureAwait(false);
        var (args, notes) = BuildArgs(preset, src, input, output);
        onCommand?.Invoke(string.Join(' ', args.Select(x => x.Contains(' ') ? $"\"{x}\"" : x)));
        if (File.Exists(output)) throw new IOException($"{output} already exists.");

        var log = new List<string>(notes);
        var started = Stopwatch.StartNew();
        var psi = NewPsi(ffmpegPath, args);
        var ok = false;
        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
            using var kill = KillOnCancel(proc, ct);
            var stderrTask = Task.Run(async () =>
            {
                while (await proc.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    lock (log)
                    {
                        if (log.Count >= MaxLogLines) log.RemoveAt(notes.Count);
                        log.Add(line);
                    }
                }
            }, CancellationToken.None);

            while (await proc.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                if (src.Duration > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan("out_time_us=".Length), out var us) && us > 0)
                    progress?.Report(Math.Min(1, us / 1_000_000.0 / src.Duration));
            }
            await stderrTask.ConfigureAwait(false);
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // A cancel kills ffmpeg, which ends the output early without an exception; never report that as done.
            ct.ThrowIfCancellationRequested();

            var text = string.Join(Environment.NewLine, log);
            if (proc.ExitCode != 0 || !File.Exists(output))
            {
                var last = log.LastOrDefault(l => l.Contains("[error]") || l.Contains("[fatal]")) ?? $"ffmpeg exited with code {proc.ExitCode}";
                return new ConvertResult(false, last, text, notes);
            }

            // A truncated output is also a small one; never let it pass as done (and so replace the original).
            var outDuration = (await ProbeAsync(ffmpegPath, output, ct).ConfigureAwait(false)).Duration;
            if (src.Duration > 0 && outDuration < src.Duration - Math.Max(1.0, src.Duration * 0.02))
                return new ConvertResult(false, $"Output is {outDuration:0.0}s but the source is {src.Duration:0.0}s", text, notes);

            ok = true;
            var time = TimeSpan.FromSeconds(Math.Round(started.Elapsed.TotalSeconds));
            return new ConvertResult(true, $"converted in {time:g}", text, notes);
        }
        finally
        {
            // ffmpeg ran with -n, so anything at the output path now is its own partial file.
            if (!ok) TryDelete(output);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private static ProcessStartInfo NewPsi(string ffmpegPath, IEnumerable<string> args)
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
        // ArgumentList quotes each argument and paths carry the file: prefix, so names cannot inject options.
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    // Redirected pipes are synchronous handles that ignore tokens; killing ffmpeg is what unblocks the reads.
    private static CancellationTokenRegistration KillOnCancel(Process proc, CancellationToken ct) => ct.Register(() =>
    {
        try { proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    });
}
