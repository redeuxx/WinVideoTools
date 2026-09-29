// Self-check for the ffmpeg downloader and startup check: a wrong hash or an oversized response must
// be rejected and leave nothing behind, and an exe that does not run must be reported.
// With --full, also downloads the real pinned build (about 250 MB).
// Usage: dotnet run --project tests/FfmpegCheck [path-to-working-ffmpeg.exe] [--full]
using System.Security.Cryptography;
using WinVideoTools;

const string SmallUrl = "https://www.gyan.dev/ffmpeg/builds/release-version";
var dir = Directory.CreateTempSubdirectory("ffmpegcheck-").FullName;
var failed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (!ok) failed++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == "" ? "" : "  " + detail)}");
}

async Task<Exception?> Try(Func<Task> run)
{
    try { await run(); return null; } catch (Exception ex) { return ex; }
}

var ex = await Try(() => Ffmpeg.DownloadAsync(SmallUrl, new string('0', 64), 1 << 20, dir, null, CancellationToken.None));
Check("wrong hash is rejected", ex is InvalidDataException && ex.Message.Contains("SHA-256"), ex?.Message ?? "no error");
Check("nothing left after rejection", Directory.GetFiles(dir).Length == 0, string.Join(", ", Directory.GetFiles(dir)));

ex = await Try(() => Ffmpeg.DownloadAsync(SmallUrl, new string('0', 64), 2, dir, null, CancellationToken.None));
Check("oversized download is cut off", ex is InvalidDataException && ex.Message.Contains("larger"), ex?.Message ?? "no error");

var fake = Path.Combine(dir, "fake.exe");
File.WriteAllText(fake, "not a program");
var probe = await Ffmpeg.ProbeAsync(fake);
Check("non-working exe is reported", probe?.Contains("could not be started") == true, probe ?? "reported as working");
File.Delete(fake);

if (args.FirstOrDefault(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) is { } given)
    Check("given ffmpeg passes the startup check", await Ffmpeg.ProbeAsync(given) is null, await Ffmpeg.ProbeAsync(given) ?? "");

if (args.Contains("--full"))
{
    // A scratch folder, so the real per-user copy is not touched.
    var real = Path.Combine(dir, "real");
    ex = await Try(() => Ffmpeg.DownloadAsync(Ffmpeg.Url, Ffmpeg.Sha256, Ffmpeg.DownloadBytes, real, null, CancellationToken.None));
    Check("real download succeeds", ex is null, ex?.Message ?? "");
    var exe = Path.Combine(real, "ffmpeg.exe");
    Check("ffmpeg.exe and LICENSE.txt extracted, zip removed",
        File.Exists(exe) && File.Exists(Path.Combine(real, "LICENSE.txt")) && !File.Exists(Path.Combine(real, "download.zip.part")),
        string.Join(", ", Directory.Exists(real) ? Directory.GetFiles(real).Select(Path.GetFileName) : []));
    if (File.Exists(exe))
        Console.WriteLine($"      ffmpeg.exe sha256 {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant()}");
}

Directory.Delete(dir, recursive: true);
return failed;
