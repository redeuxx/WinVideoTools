# WinVideoTools

A Windows desktop app for managing a video collection. It has three tools, all powered by [FFmpeg](https://ffmpeg.org):

- **Video Verifier** finds broken or truncated videos.
- **Similar Videos** finds duplicate copies of the same video, even when they were re-encoded or resized.
- **Convert** re-encodes videos using your HandBrake presets.

Built with WinUI 3 and .NET 10.

## Requirements

- Windows 10 version 2004 (build 19041) or later, x64
- FFmpeg. If it isn't found, the app offers to download a pinned build (about 250 MB) and checks it against a known SHA-256 hash before using it. The download goes to `%LOCALAPPDATA%\WinVideoTools\ffmpeg`.

## Tools

### Video Verifier

Choose a folder (or drop one onto the window) and click **Scan**. Every video is fully decoded with FFmpeg, and each file gets one of these results:

| Status | Meaning |
| --- | --- |
| **Valid** (green) | Decoded cleanly from start to finish. |
| **Valid, errors** (amber) | Plays normally but has a few tiny glitches, such as damaged ASF packet headers in old WMV files. The details pane explains what was found. Converting the file gives you a clean copy. |
| **Invalid** (red) | FFmpeg reported decode errors, or the file decoded noticeably shorter than its header says, which usually means it was cut off. |
| **Error** (red) | The file couldn't be checked at all. |

Select a file to see the FFmpeg log for it. You can also:

- Press **Refresh** (F5) to check only new or changed files.
- Use **Export CSV** to save the results.
- Use **Clear list** to empty the results.
- Select files and move them to the Recycle Bin, move them to another folder, or delete them permanently.

### Similar Videos

Add one or more folders and click **Scan**. The tool fingerprints short bursts of frames sampled through each video, then groups videos that look alike. It still finds copies that were re-encoded, resized, letterboxed, put in a different container, or trimmed by a few seconds. It won't find clips cut from the middle of a longer video, or copies that are mirrored or heavily cropped.

**Max drift** sets how far apart in time the same moment can be in two copies. Higher values catch more trimmed copies, but scans are slower and more unrelated videos may be matched.

**Select all but best copy** keeps the highest-resolution file in each group (the smallest one if resolutions tie) and selects the rest so you can remove them.

### Convert

Import presets from a HandBrake preset file, add files or folders, and click **Convert**. The app turns each HandBrake preset into an FFmpeg command. HandBrake has its own encoding engine, so the result is close to what HandBrake would produce but not identical. Settings with no FFmpeg equivalent are skipped, and the log says so.

Under **Options** you can:

- Skip files that are already HEVC (H.265).
- Delete the output if it ended up larger than the original.
- Delete the original if the output is smaller. You have to confirm this each time you convert.

You can also pause the queue after the current file, or have Windows shut down when the queue finishes (after a 60-second countdown you can cancel).

## Building

Requires the .NET 10 SDK.

```powershell
.\run.ps1                          # build and launch (Debug)
.\run.ps1 -Configuration Release
.\run.ps1 -NoBuild                 # launch the last build
```

To include FFmpeg in the build output, put `ffmpeg.exe` at `src/WinVideoTools/ffmpeg/ffmpeg.exe`. It's git-ignored. If the file is there, the app uses it instead of downloading one.

## Tests

Each tool has a small self-check program under `tests/`. The checks generate test videos with FFmpeg and verify the results. Each prints PASS or FAIL per case, and the exit code is the number of failures.

```powershell
dotnet run --project tests/VideoCheckerCheck  [path\to\ffmpeg.exe]
dotnet run --project tests/SimilarVideosCheck [path\to\ffmpeg.exe]
dotnet run --project tests/ConverterCheck     [path\to\ffmpeg.exe]
dotnet run --project tests/FfmpegCheck        # add --full to also download the real FFmpeg build
```

If no path is given, the checks use `ffmpeg` from your PATH.

## License

MIT. See [LICENSE](LICENSE).

FFmpeg is a separate program licensed under the LGPL or GPL, depending on the build, and isn't covered by this license. See https://ffmpeg.org/legal.html.
