// Self-check for the HandBrake preset converter: preset import/store, ffmpeg argument mapping,
// and a real conversion of a generated clip.
// Usage: dotnet run --project tests/ConverterCheck [absolute-path-to-ffmpeg.exe]  (default: ffmpeg on PATH)
using System.Diagnostics;
using WinVideoTools.Converter;

var ffmpeg = args.Length > 0 ? args[0] : "ffmpeg";
var dir = Directory.CreateTempSubdirectory("convertcheck-").FullName;
string P(string name) => Path.Combine(dir, name);
var failed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (!ok) failed++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == "" ? "" : "  " + detail)}");
}

void Ffmpeg(params string[] a)
{
    var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false };
    foreach (var x in (string[])["-hide_banner", "-v", "error", "-y", .. a]) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception("ffmpeg failed: " + string.Join(' ', a));
}

bool Has(List<string> a, params string[] seq) =>
    Enumerable.Range(0, a.Count - seq.Length + 1).Any(i => seq.Select((s, j) => a[i + j] == s).All(x => x));

// The user's HandBrake export (default.json), trimmed to the keys the converter reads.
const string DefaultPreset = """
{
  "PresetList": [{
    "AudioCopyMask": ["copy:aac"], "AudioEncoderFallback": "av_aac", "AudioLanguageList": ["eng"],
    "AudioList": [{ "AudioBitrate": 160, "AudioEncoder": "av_aac", "AudioMixdown": "stereo", "AudioSamplerate": "auto", "AudioTrackGainSlider": 0, "AudioTrackDRCSlider": 0 }],
    "AudioSecondaryEncoderMode": true, "AudioTrackSelectionBehavior": "first", "ChapterMarkers": true,
    "FileFormat": "av_mkv", "Folder": false, "PictureCropMode": 0,
    "PictureDeinterlaceFilter": "decomb", "PictureCombDetectPreset": "default", "PictureDeinterlacePreset": "default",
    "PictureDenoiseFilter": "off", "PictureSharpenFilter": "off", "PictureDetelecine": "off", "PictureDeblockPreset": "off",
    "PictureColorspacePreset": "off", "PictureChromaSmoothPreset": "off", "PicturePadMode": "none",
    "PictureKeepRatio": true, "PictureWidth": 1920, "PictureHeight": 1080, "PictureUseMaximumSize": true, "PictureAllowUpscaling": false,
    "PresetName": "default", "SubtitleAddForeignAudioSearch": true, "SubtitleBurnBehavior": "none",
    "SubtitleLanguageList": ["eng"], "SubtitleTrackSelectionBehavior": "first",
    "VideoEncoder": "nvenc_h265", "VideoFramerateMode": "vfr", "VideoPreset": "slower", "VideoTune": "", "VideoProfile": "auto",
    "VideoLevel": "auto", "VideoOptionExtra": "", "VideoQualityType": 2, "VideoQualitySlider": 24.5, "MetadataPassthru": true
  }],
  "VersionMajor": 72, "VersionMicro": 0, "VersionMinor": 0
}
""";

// IMPORT AND STORE

File.WriteAllText(P("default.json"), DefaultPreset);
var preset = HandBrakePreset.ReadFile(P("default.json")).Single();
Check("reads preset name", preset.Name == "default");

var store = new PresetStore(P("store"));
store.Save(preset);
var (loaded, errors) = store.Load();
Check("store round-trips", loaded.Count == 1 && errors.Count == 0 && loaded[0].Str("VideoEncoder") == "nvenc_h265");
Check("saved file is HandBrake-importable", HandBrakePreset.ReadFile(store.PathFor("default")).Single().Num("VideoQualitySlider") == 24.5
    && File.ReadAllText(store.PathFor("default")).Contains("\"VersionMajor\": 72"));
Check("traversal name stays in folder", Path.GetDirectoryName(store.PathFor(@"..\..\evil"))!.Equals(Path.GetFullPath(P("store")), StringComparison.OrdinalIgnoreCase));
Check("device name is escaped", Path.GetFileName(store.PathFor("CON")) == "_CON.json");
store.Delete("default");
Check("delete removes preset", store.Load().Presets.Count == 0);

File.WriteAllText(P("folders.json"), """
{ "PresetList": [ { "Folder": true, "PresetName": "General", "ChildrenArray": [ { "PresetName": "A" }, { "PresetName": "B" } ] } ], "VersionMajor": 72 }
""");
Check("folders are flattened", string.Join(",", HandBrakePreset.ReadFile(P("folders.json")).Select(p => p.Name)) == "A,B");
File.WriteAllText(P("bad.json"), "{ \"nope\": 1 }");
Check("non-preset JSON is rejected", Throws<InvalidDataException>(() => HandBrakePreset.ReadFile(P("bad.json"))));

// MAPPING

var (clean, dropped) = PresetConverter.SanitizeEncoderOptions(@"ref=4:dump-yuv=out.yuv:csv=C\x.csv:aq-mode=3:qpfile=q.txt");
Check("file options stripped", clean == "ref=4:aq-mode=3" && dropped.Count == 3, clean);

var info = PresetConverter.ParseInfo("""
  Duration: 00:01:00.50, start: 0.000000, bitrate: 1000 kb/s
  Stream #0:0: Video: h264 (High), yuv420p, 3840x2160
  Stream #0:1(fre): Audio: ac3, 48000 Hz, 5.1
  Stream #0:2[0x2](eng): Audio: aac (LC), 48000 Hz, stereo
  Stream #0:3(eng): Audio: aac (LC), 48000 Hz, stereo
  Stream #0:4(ger): Subtitle: hdmv_pgs_subtitle (pgssub)
  Stream #0:5(eng): Subtitle: subrip (srt)
  Stream #0:6: Video: mjpeg (Baseline), yuvj420p, 600x800 (attached pic)
""");
Check("reads video size, ignores codec tags", info.Streams[0] is { Width: 3840, Height: 2160 } && info.Streams[1].Width == 0
    && PresetConverter.ParseInfo("  Stream #0:0[0x1](und): Video: hevc (Main 10) (hvc1 / 0x31637668), yuv420p10le(tv), 1920x1080 [SAR 1:1 DAR 16:9], 24 fps")
        .Streams[0] is { Width: 1920, Height: 1080 });

SourceInfo Hevc(int w, int h) => new(10, [new SourceStream(0, "Video", "", "hevc", false, w, h)]);
Check("skip HEVC at any size", PresetConverter.ShouldSkipHevc(Hevc(3840, 2160), onlyUpTo1080p: false));
Check("1080p limit: skips 1080p, portrait and ultrawide", PresetConverter.ShouldSkipHevc(Hevc(1920, 1080), true)
    && PresetConverter.ShouldSkipHevc(Hevc(1080, 1920), true) && PresetConverter.ShouldSkipHevc(Hevc(2560, 1080), true));
Check("1080p limit: converts 4K and unknown size", !PresetConverter.ShouldSkipHevc(Hevc(3840, 2160), true) && !PresetConverter.ShouldSkipHevc(Hevc(0, 0), true));
Check("never skips non-HEVC", !PresetConverter.ShouldSkipHevc(info, false));
Check("parses duration and streams", info.Duration == 60.5 && info.Streams.Count == 7 && info.Streams[6].AttachedPic && info.Streams[2].Language == "eng");
Check("picks first matching track", PresetConverter.SelectTracks(info.Streams.Where(s => s.Kind == "Audio"), "first", ["eng"]).Single().Index == 2);
Check("B/T language codes match", PresetConverter.SelectTracks(info.Streams.Where(s => s.Kind == "Subtitle"), "first", ["deu"]).Single().Index == 4);

var (a, notes) = PresetConverter.BuildArgs(preset, info, @"C:\in.mkv", @"C:\out.mkv");
var cmd = string.Join(' ', a);
Check("nvenc CQ 24.5 slower", Has(a, "-c:v", "hevc_nvenc") && Has(a, "-rc", "vbr", "-cq", "24.5", "-b:v", "0") && Has(a, "-preset", "p6"), cmd);
Check("scales down to 1080p max", cmd.Contains("scale=w='min(1920,iw)':h='min(1080,ih)':force_original_aspect_ratio=decrease"), cmd);
Check("decomb maps to bwdif", cmd.Contains("bwdif=mode=send_frame:deint=interlaced"), cmd);
Check("english AAC 160k stereo", Has(a, "-map", "0:2", "-c:a:0", "aac") && Has(a, "-b:a:0", "160k") && Has(a, "-ac:a:0", "2") && !Has(a, "-map", "0:3"), cmd);
Check("english subtitle copied", Has(a, "-map", "0:5", "-c:s:0", "copy") && !Has(a, "-map", "0:4"), cmd);
Check("VFR passthrough, mkv out", Has(a, "-fps_mode", "passthrough") && Has(a, "-f", "matroska", "file:C:\\out.mkv"), cmd);
Check("flags auto crop and foreign audio search", PresetConverter.Unsupported(preset) is var u && u.Count == 2, string.Join(" | ", PresetConverter.Unsupported(preset)));

preset.Json["AudioList"]![0]!["AudioEncoder"] = "copy";
preset.Json["FileFormat"] = "av_mp4";
(a, notes) = PresetConverter.BuildArgs(preset, info with { Streams = info.Streams.Where(s => s.Index != 5).ToList() }, "in", "out");
Check("auto passthru copies AAC", Has(a, "-map", "0:2", "-c:a:0", "copy"), string.Join(' ', a));
preset.Json["SubtitleLanguageList"] = new System.Text.Json.Nodes.JsonArray("ger");
(a, notes) = PresetConverter.BuildArgs(preset, info, "in", "out");
Check("mp4 skips image subs with a note", !Has(a, "-map", "0:4") && notes.Any(n => n.Contains("image subtitles")), string.Join(" | ", notes));

// REAL CONVERSION

Ffmpeg("-f", "lavfi", "-i", "testsrc=duration=3:size=1280x720:rate=30",
       "-f", "lavfi", "-i", "sine=duration=3:frequency=440", "-f", "lavfi", "-i", "sine=duration=3:frequency=880",
       "-map", "0", "-map", "1", "-map", "2", "-c:v", "libx264", "-c:a", "ac3",
       "-metadata:s:a:0", "language=fre", "-metadata:s:a:1", "language=eng", P("src.mkv"));
File.Copy(P("src.mkv"), P("-odd name ü.mkv"));

var x264 = HandBrakePreset.ReadFile(P("default.json")).Single();
x264.Json["VideoEncoder"] = "x264";
x264.Json["VideoPreset"] = "veryfast";
var outPath = PresetConverter.OutputPathFor(P("-odd name ü.mkv"), null, x264);
Check("output never overwrites source", outPath.EndsWith("-odd name ü (2).mkv"), outPath);
var r = await PresetConverter.ConvertAsync(ffmpeg, x264, P("-odd name ü.mkv"), outPath, null, null, CancellationToken.None);
var outInfo = r.Success ? await PresetConverter.ProbeAsync(ffmpeg, outPath, CancellationToken.None) : null;
Check("x264 conversion succeeds", r.Success, r.Summary + Environment.NewLine + r.Log);
Check("output has h264 + english aac only", outInfo is not null
    && outInfo.Streams.Count(s => s.Kind == "Audio") == 1 && outInfo.Streams.Any(s => s.Kind == "Audio" && s.Codec == "aac" && s.Language == "eng")
    && outInfo.Streams.Any(s => s.Kind == "Video" && s.Codec == "h264"), string.Join(", ", outInfo?.Streams.Select(s => $"{s.Kind}:{s.Codec}:{s.Language}") ?? []));

var nvOut = P("nvenc.mkv");
r = await PresetConverter.ConvertAsync(ffmpeg, HandBrakePreset.ReadFile(P("default.json")).Single(), P("src.mkv"), nvOut, null, null, CancellationToken.None);
if (r.Success) Check("nvenc conversion produces hevc", (await PresetConverter.ProbeAsync(ffmpeg, nvOut, CancellationToken.None)).Streams.Any(s => s.Codec == "hevc"));
else Console.WriteLine($"SKIP  nvenc conversion (no NVIDIA encoder here?): {r.Summary}");
Check("failed run leaves no partial file", r.Success || !File.Exists(nvOut));

using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
{
    var slow = HandBrakePreset.ReadFile(P("default.json")).Single();
    slow.Json["VideoEncoder"] = "x265";
    slow.Json["VideoPreset"] = "placebo";
    var cancelled = false;
    try { await PresetConverter.ConvertAsync(ffmpeg, slow, P("src.mkv"), P("cancel.mkv"), null, null, cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check("cancel throws and removes partial output", cancelled && !File.Exists(P("cancel.mkv")));
}

Directory.Delete(dir, recursive: true);
return failed;

static bool Throws<T>(Action act) where T : Exception
{
    try { act(); return false; } catch (T) { return true; }
}
