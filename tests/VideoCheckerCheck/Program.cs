// Self-check for VideoChecker: builds valid and truncated videos with ffmpeg, asserts the verdicts.
// Usage: dotnet run --project tests/VideoCheckerCheck [absolute-path-to-ffmpeg.exe]  (default: ffmpeg on PATH)
using System.Diagnostics;
using WinVideoTools.VideoVerifier;

var ffmpeg = args.Length > 0 ? args[0] : "ffmpeg";
var dir = Directory.CreateTempSubdirectory("videocheck-").FullName;

void Ffmpeg(params string[] a)
{
    var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false };
    foreach (var x in (string[])["-hide_banner", "-v", "error", "-y", .. a]) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception("ffmpeg failed: " + string.Join(' ', a));
}

void Truncate(string src, string dst)
{
    var bytes = File.ReadAllBytes(Path.Combine(dir, src));
    File.WriteAllBytes(Path.Combine(dir, dst), bytes[..(int)(bytes.Length * 0.6)]);
}

string P(string name) => Path.Combine(dir, name);

Ffmpeg("-f", "lavfi", "-i", "testsrc=duration=10:size=320x240:rate=30", "-f", "lavfi", "-i", "sine=duration=10",
       "-c:v", "libx264", "-c:a", "aac", "-shortest", P("ok.mp4"));
Ffmpeg("-i", P("ok.mp4"), "-c", "copy", "-movflags", "+faststart", P("fast.mp4"));
Ffmpeg("-i", P("ok.mp4"), "-c", "copy", P("ok.mkv"));
Truncate("ok.mp4", "trunc.mp4");
Truncate("fast.mp4", "truncfast.mp4");
Truncate("ok.mkv", "trunc.mkv");
File.Copy(P("ok.mp4"), P("-weird name ü.mp4"));
// Repeated timestamps play fine but make the null muxer log errors; that must not fail the file.
Ffmpeg("-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=30", "-vf", "setpts='if(between(N,60,90),60,N)/30/TB'",
       "-fps_mode", "passthrough", "-c:v", "libx264", P("dupts.mkv"));
// Damaged bytes mid-file are real decode errors and must still fail.
var clean = File.ReadAllBytes(P("ok.mkv"));
for (var i = clean.Length / 3; i < clean.Length / 3 + 4096; i += 128) clean[i] ^= 0xFF;
File.WriteAllBytes(P("damaged.mkv"), clean);

var expected = new Dictionary<string, bool>
{
    ["ok.mp4"] = true, ["fast.mp4"] = true, ["ok.mkv"] = true, ["-weird name ü.mp4"] = true,
    ["dupts.mkv"] = true, ["damaged.mkv"] = false,
    ["trunc.mp4"] = false, ["truncfast.mp4"] = false, ["trunc.mkv"] = false,
};

var failed = 0;

// Tiny ASF/WMV glitches seen in files that play fine are minor; big overruns and other errors are not.
foreach (var (msg, minor) in new (string, bool)[]
{
    ("packet_obj_size -1779179237 invalid", true),
    ("Error or Bits overconsumption: 140840 > 140832 at 30x40", true),
    ("Error or Bits overconsumption: 150000 > 140832 at 30x40", false),
    ("Invalid NAL unit size (1234 > 567).", false),
})
{
    var ok = VideoChecker.IsMinorError(msg) == minor;
    if (!ok) failed++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  minor={minor}  {msg}");
}
foreach (var (name, valid) in expected)
{
    var r = await VideoChecker.CheckAsync(ffmpeg, P(name), null, CancellationToken.None);
    var ok = r.IsValid == valid;
    if (!ok) failed++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name,-20} valid={r.IsValid}  {r.Summary}");
}

Directory.Delete(dir, recursive: true);
return failed;
