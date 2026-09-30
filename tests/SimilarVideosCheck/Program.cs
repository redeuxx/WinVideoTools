// Self-check for VideoFingerprint: builds re-encoded, resized and trimmed copies plus unrelated videos
// with ffmpeg, then asserts which ones get grouped together.
// Usage: dotnet run --project tests/SimilarVideosCheck [absolute-path-to-ffmpeg.exe]  (default: ffmpeg on PATH)
using System.Diagnostics;
using WinVideoTools.SimilarVideos;

var ffmpeg = args.Length > 0 ? args[0] : "ffmpeg";
var dir = Directory.CreateTempSubdirectory("similarcheck-").FullName;
var failed = 0;

void Check(bool ok, string what)
{
    if (!ok) failed++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
}

void Ffmpeg(params string[] a)
{
    var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false };
    foreach (var x in (string[])["-hide_banner", "-v", "error", "-y", .. a]) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception("ffmpeg failed: " + string.Join(' ', a));
}

string P(string name) => Path.Combine(dir, name);

// DHASH
Check(VideoFingerprint.DHash(new byte[72]) is null, "flat frame has no hash");
var ramp = Enumerable.Range(0, 72).Select(i => (byte)(i % 9 * 30)).ToArray();
Check(VideoFingerprint.DHash(ramp) == ulong.MaxValue, "left-to-right ramp hashes to all ones");

// CROP
const int G = VideoFingerprint.GrabSize;
var picture = Enumerable.Range(0, G * G).Select(i => (byte)(64 + i % G * 2)).ToArray();
var boxed = new byte[G * G];
Array.Copy(picture, 0, boxed, 8 * G, 48 * G); // rows 0-47 of the picture moved down between 8-row bars
Check(VideoFingerprint.CropAndShrink(boxed).SequenceEqual(VideoFingerprint.CropAndShrink(picture[..(48 * G)].Concat(new byte[16 * G]).ToArray())),
      "letterboxed and bottom-barred frames shrink the same");
Check(VideoFingerprint.DHash(VideoFingerprint.CropAndShrink(new byte[G * G])) is null, "all-black frame has no hash");

// GROUPING
Ffmpeg("-f", "lavfi", "-i", "testsrc2=duration=30:size=640x360:rate=25", "-c:v", "libx264", P("a.mp4"));
Ffmpeg("-i", P("a.mp4"), "-vf", "scale=320:180", "-c:v", "libx264", "-crf", "35", P("a small.mkv"));
Ffmpeg("-ss", "1", "-i", P("a.mp4"), "-c:v", "libx264", P("a trimmed.mp4"));
Ffmpeg("-f", "lavfi", "-i", "mandelbrot=size=640x360:rate=25", "-t", "30", "-c:v", "libx264", P("b.mp4"));
Ffmpeg("-i", P("a.mp4"), "-vf", "pad=640:480:0:60:black", "-c:v", "libx264", P("a boxed.mp4"));
Ffmpeg("-i", P("a.mp4"), "-vf", "pad=640:400:0:0:black", "-c:v", "libx264", P("a bottom bar.mkv"));
Ffmpeg("-f", "lavfi", "-i", "testsrc2=duration=90:size=640x360:rate=25", "-c:v", "libx264", P("a longer.mp4"));
Ffmpeg("-f", "lavfi", "-i", "color=black:duration=30:size=640x360:rate=25", "-c:v", "libx264", P("black.mp4"));
Ffmpeg("-f", "lavfi", "-i", "color=black:duration=30:size=640x360:rate=25", "-c:v", "libx264", "-crf", "40", P("black2.mp4"));

string[] names = ["a.mp4", "a small.mkv", "a trimmed.mp4", "a boxed.mp4", "a bottom bar.mkv", "b.mp4", "a longer.mp4", "black.mp4", "black2.mp4"];
var prints = new List<VideoFingerprint>();
foreach (var n in names)
{
    var fp = await VideoFingerprint.ComputeAsync(ffmpeg, P(n), VideoFingerprint.DefaultMaxDrift, CancellationToken.None);
    Console.WriteLine($"      {n,-15} {fp.Width}x{fp.Height} {fp.Duration:0.0}s {fp.Samples.Length} samples");
    prints.Add(fp);
}

var groups = VideoFingerprint.Group(prints)
    .Select(g => string.Join(", ", g.Select(i => names[i]).Order()))
    .ToList();
Console.WriteLine("      groups: " + string.Join(" | ", groups));
Check(groups.Count == 1 && groups[0] == "a bottom bar.mkv, a boxed.mp4, a small.mkv, a trimmed.mp4, a.mp4",
      "only re-encoded, resized, trimmed and letterboxed copies grouped");

// DETAILS
Check(prints[0].Codec == "h264", "codec read from stream info");
Check(prints[0].VideoBitrate > 0 && prints[1].VideoBitrate == 0, "video bit rate read from MP4, absent in MKV");
var jpeg = await VideoFingerprint.ScreenshotAsync(ffmpeg, P("a.mp4"), prints[0].Duration / 2, CancellationToken.None);
Check(jpeg is [0xFF, 0xD8, ..], "screenshot is a JPEG");

var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
try
{
    await VideoFingerprint.ComputeAsync(ffmpeg, P("a longer.mp4"), VideoFingerprint.DefaultMaxDrift, cts.Token);
    Check(false, "cancellation throws");
}
catch (OperationCanceledException)
{
    Check(true, "cancellation throws");
}

Directory.Delete(dir, recursive: true);
return failed;
