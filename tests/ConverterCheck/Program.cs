// Self-check for the HandBrake preset converter: preset import/store, ffmpeg argument mapping,
// and a real conversion of a generated clip.
// Usage: dotnet run --project tests/ConverterCheck [absolute-path-to-ffmpeg.exe]  (default: ffmpeg on PATH)
using System.Diagnostics;
using System.Text.Json;
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

// SESSION

File.WriteAllText(P("out.mkv"), "");
new ConvertSession([dir, "relative"], [
    new SessionItem(P("a.mkv"), 10, ConvertStatus.Done, "ok", "Kept both", "log", P("out.mkv"), 5, 0.5, false),
    new SessionItem(P("b.mkv"), 20, ConvertStatus.Converting, "", "", "", P("gone.mkv"), null, null, false),
    new SessionItem(P("A.MKV"), 30, ConvertStatus.Queued, "", "", "", null, null, null, false),
    new SessionItem("rel.mkv", 1, ConvertStatus.Queued, "", "", "", null, null, null, false),
    new SessionItem(P("evil.exe"), 1, ConvertStatus.Queued, "", "", "", P("out.exe"), null, null, false),
]).Write(P("session.json"));
var session = ConvertSession.Read(P("session.json"));
Check("session keeps absolute videos once", session.Items.Count == 2 && session.Folders.SequenceEqual([dir]), string.Join(", ", session.Items.Select(i => i.Path)));
Check("session round-trips results", session.Items[0] is { Status: ConvertStatus.Done, Decision: "Kept both", OutputSize: 5, SizeRatio: 0.5 } && session.Items[0].OutputPath == P("out.mkv"));
Check("interrupted file is queued, missing output forgotten", session.Items[1] is { Status: ConvertStatus.Queued, OutputPath: null });
Check("session write leaves no temp file", !File.Exists(P("session.json.tmp")));
Check("session without options reads as null options", session.Options is null);
var opts = new ConvertOptions("default", dir, false, true, true, true, true, false);
new ConvertSession([], [], opts).Write(P("opts.json"));
Check("session options round-trip", ConvertSession.Read(P("opts.json")).Options == opts);
new ConvertSession([], [], opts with { OutputFolder = "relative" }).Write(P("opts.json"));
new ConvertSession([dir + "\0x"], [
    new SessionItem(P("nul\0.mkv"), 1, ConvertStatus.Queued, "", "", "", null, null, null, false),
    new SessionItem(P("unc.mkv"), 1, ConvertStatus.Done, "", "", "", @"\\no-such-host.invalid\share\out.mkv", null, null, false),
], opts with { OutputFolder = "C:\\a\0b" }).Write(P("odd.json"));
var odd = ConvertSession.Read(P("odd.json"));
Check("NUL paths are dropped, not a crash", odd.Items.Count == 1 && odd.Folders.Count == 0 && odd.Options?.OutputFolder == "");
Check("network output kept without probing", odd.Items[0].OutputPath == @"\\no-such-host.invalid\share\out.mkv");
Check("relative output folder dropped", ConvertSession.Read(P("opts.json")).Options is { OutputFolder: "", Preset: "default" });
Check("non-session JSON is rejected", Throws<InvalidDataException>(() => ConvertSession.Read(P("bad.json"))));
new ConvertSession([], [new SessionItem(P("s.mkv"), 1, ConvertStatus.Skipped, "Skipped", "", "", null, null, null, false, true)]).Write(P("skip.json"));
Check("1080p skip round-trips", ConvertSession.Read(P("skip.json")).Items[0] is { Status: ConvertStatus.Skipped, SkippedUpTo1080p: true });
File.WriteAllText(P("old.json"), $$"""{ "Folders": [], "Items": [ { "Path": {{JsonSerializer.Serialize(P("s.mkv"))}}, "Status": "Skipped" } ] }""");
Check("older session reads as not known 1080p", ConvertSession.Read(P("old.json")).Items[0] is { Status: ConvertStatus.Skipped, SkippedUpTo1080p: false });

// HISTORY

File.WriteAllText(P("h1.mkv"), "first");
File.WriteAllText(P("h2.mkv"), "skipped");
File.WriteAllText(P("h3.mkv"), "output");
var stat1 = ConvertHistory.Stat(new FileInfo(P("h1.mkv")));
var history = new ConvertHistory();
history.Record(P("h1.mkv"), HistoryOutcome.Discarded, stat1!.Value.Size, stat1.Value.Modified);
var stat2 = ConvertHistory.Stat(new FileInfo(P("h2.mkv")))!.Value;
history.Record(P("h2.mkv"), HistoryOutcome.Skipped, stat2.Size, stat2.Modified);
var stat3 = ConvertHistory.Stat(new FileInfo(P("h3.mkv")))!.Value;
history.Record(P("h3.mkv"), HistoryOutcome.Output, stat3.Size, stat3.Modified);
history.AddFolder(dir);
history.AddFolder(dir.ToUpperInvariant());
Check("discarded file is left out", history.IsProcessed(P("h1.mkv"), stat1, false, false));
Check("lookup ignores case", history.IsProcessed(P("H1.MKV"), stat1, false, false));
Check("written output is left out", history.IsProcessed(P("h3.mkv"), stat3, false, false));
Check("unknown file is queued", !history.IsProcessed(P("new.mkv"), stat1, false, false) && !history.Changed(P("new.mkv"), stat1));
Check("missing file is neither processed nor changed", !history.IsProcessed(P("h1.mkv"), null, false, false) && !history.Changed(P("h1.mkv"), null));
Check("skip counts only while the options would skip again", history.IsProcessed(P("h2.mkv"), stat2, true, false)
    && !history.IsProcessed(P("h2.mkv"), stat2, false, false) && !history.IsProcessed(P("h2.mkv"), stat2, true, true));
Check("folders kept once", history.Folders.Count == 1);

ConvertHistory.WriteJson(P("history.json"), history.ToJson());
var reread = ConvertHistory.Read(P("history.json"));
Check("history round-trips with exact times", reread.Count == 3 && reread.IsProcessed(P("h1.mkv"), ConvertHistory.Stat(new FileInfo(P("h1.mkv"))), false, false)
    && reread.Find(P("h2.mkv"))?.Outcome == HistoryOutcome.Skipped && reread.Folders.SequenceEqual([dir]));
Check("history write leaves no temp file", !File.Exists(P("history.json.tmp")));

File.AppendAllText(P("h1.mkv"), " and changed");
var changed = ConvertHistory.Stat(new FileInfo(P("h1.mkv")));
Check("changed size is queued again", !reread.IsProcessed(P("h1.mkv"), changed, false, false) && reread.Changed(P("h1.mkv"), changed));
File.SetLastWriteTimeUtc(P("h2.mkv"), DateTime.UtcNow.AddDays(1));
var touched = ConvertHistory.Stat(new FileInfo(P("h2.mkv")));
Check("changed time is queued again", !reread.IsProcessed(P("h2.mkv"), touched, true, false) && reread.Changed(P("h2.mkv"), touched));

var other = new ConvertHistory();
other.Record(P("h1.mkv"), HistoryOutcome.Converted, changed!.Value.Size, changed.Value.Modified);
other.AddFolder(P("more"));
reread.Merge(other);
Check("import adds folders and its entries win", reread.Find(P("h1.mkv"))?.Outcome == HistoryOutcome.Converted
    && reread.IsProcessed(P("h1.mkv"), changed, false, false) && reread.Folders.Count == 2 && reread.Count == 3);

File.WriteAllText(P("odd-history.json"), $$"""
{ "Folders": ["relative", {{JsonSerializer.Serialize(dir + "\0x")}}, {{JsonSerializer.Serialize(dir)}}],
  "Files": {
    "rel.mkv": { "Outcome": "Converted", "Size": 1, "Modified": "2026-01-01T00:00:00Z" },
    {{JsonSerializer.Serialize(P("evil.exe"))}}: { "Outcome": "Converted", "Size": 1, "Modified": "2026-01-01T00:00:00Z" },
    {{JsonSerializer.Serialize(P("neg.mkv"))}}: { "Outcome": "Converted", "Size": -1, "Modified": "2026-01-01T00:00:00Z" },
    {{JsonSerializer.Serialize(P("null.mkv"))}}: null,
    {{JsonSerializer.Serialize(P("ok.mkv"))}}: { "Outcome": "Skipped", "Size": 1, "Modified": "2026-01-01T00:00:00Z" }
  } }
""");
var odd2 = ConvertHistory.Read(P("odd-history.json"));
Check("imported history keeps only absolute videos and folders", odd2.Count == 1 && odd2.Find(P("ok.mkv")) is not null && odd2.Folders.SequenceEqual([dir]));
Check("non-history JSON is rejected", Throws<InvalidDataException>(() => ConvertHistory.Read(P("bad.json")))
    && Throws<InvalidDataException>(() => ConvertHistory.Read(P("session.json"))));
reread.Clear();
Check("clear forgets files and folders", reread.Count == 0 && reread.Folders.Count == 0);

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
Check("skip stands under the same or broader rule", PresetConverter.StillSkipped(false, true, false) && PresetConverter.StillSkipped(true, true, true));
Check("skip rechecked when the rule narrows or is off", !PresetConverter.StillSkipped(false, true, true) && !PresetConverter.StillSkipped(true, false, false));
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
Check("success renames the temporary file", File.Exists(outPath) && !File.Exists(outPath + ".partial"));
Check("output has h264 + english aac only", outInfo is not null
    && outInfo.Streams.Count(s => s.Kind == "Audio") == 1 && outInfo.Streams.Any(s => s.Kind == "Audio" && s.Codec == "aac" && s.Language == "eng")
    && outInfo.Streams.Any(s => s.Kind == "Video" && s.Codec == "h264"), string.Join(", ", outInfo?.Streams.Select(s => $"{s.Kind}:{s.Codec}:{s.Language}") ?? []));

var nvOut = P("nvenc.mkv");
r = await PresetConverter.ConvertAsync(ffmpeg, HandBrakePreset.ReadFile(P("default.json")).Single(), P("src.mkv"), nvOut, null, null, CancellationToken.None);
if (r.Success) Check("nvenc conversion produces hevc", (await PresetConverter.ProbeAsync(ffmpeg, nvOut, CancellationToken.None)).Streams.Any(s => s.Codec == "hevc"));
else Console.WriteLine($"SKIP  nvenc conversion (no NVIDIA encoder here?): {r.Summary}");
Check("failed run leaves no partial file", r.Success || (!File.Exists(nvOut) && !File.Exists(nvOut + ".partial")));

using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
{
    var slow = HandBrakePreset.ReadFile(P("default.json")).Single();
    slow.Json["VideoEncoder"] = "x265";
    slow.Json["VideoPreset"] = "placebo";
    var cancelled = false;
    try { await PresetConverter.ConvertAsync(ffmpeg, slow, P("src.mkv"), P("cancel.mkv"), null, null, cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check("cancel throws and removes partial output", cancelled && !File.Exists(P("cancel.mkv")) && !File.Exists(P("cancel.mkv.partial")));
}

Directory.Delete(dir, recursive: true);
return failed;

static bool Throws<T>(Action act) where T : Exception
{
    try { act(); return false; } catch (T) { return true; }
}
