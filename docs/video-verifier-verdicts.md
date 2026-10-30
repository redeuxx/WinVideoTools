# Video Verifier verdicts

The Video Verifier decodes every frame of a file with ffmpeg (`ffmpeg -i file -f null -`) and reads the log. This page explains which files count as fine and why. The code lives in [VideoChecker.cs](../src/WinVideoTools/VideoVerifier/VideoChecker.cs), and the self-test in [tests/VideoCheckerCheck](../tests/VideoCheckerCheck/Program.cs) builds a sample file for each case.

## The rule

**A file that plays fine, and that converting would turn into a clean copy, is not invalid.** It may be flagged, but it is not marked as broken. Only files that are actually unusable or incomplete are marked invalid.

## Statuses

| Status | Color | Meaning |
|---|---|---|
| Valid | Green | Decoded with no errors. |
| Valid, errors | Yellow | Plays fine, but has minor glitches or a small damaged spot. Converting gives a copy that decodes without errors. |
| Invalid | Red | Real damage: the file ends early, ffmpeg fails, or the damage is too large to call minor. |
| Error | Red | The check itself could not run. |

Both Valid and "Valid, errors" count as valid in the totals, the filter and "Select valid".

## Ignored (still green)

These messages say nothing about whether the file decodes, so they are logged but never fail the file:

- **Messages from the null muxer** (`[null @ ...]`). The null muxer is our throwaway output. It complains about things like repeated timestamps, which are common in variable frame rate files and screen recordings.
- **`Referenced QT chapter track not found`**. An MP4 points to a chapter track that is missing. Only the chapter markers are lost.

## Minor errors (yellow)

Old ASF/WMV files often have tiny damage that players hide completely:

- `packet_obj_size ... invalid`: a damaged packet header, so that packet is skipped.
- `Error or Bits overconsumption: X > Y`, where X is at most 64 bits more than Y: a frame read a few bits past its data and decoded almost entirely correctly. A larger overrun is real damage.

## Small damage (yellow)

A stretch of a file can be zeroed out or garbled, for example after an incomplete download or a bad disk sector. ffmpeg then logs errors such as `Invalid NAL unit size (0 > 64104)`, `channel element 0.0 duplicate` or `corrupt decoded frame`. Players skip the bad spot as a brief freeze, smear or audio dropout, and the rest of the file is intact.

A damaged file is yellow instead of red when all of these hold:

- It decodes to its full length (see "Ends early" below).
- ffmpeg exits normally.
- Damaged packets are no more than the **allowed damage** share of the video frames. A damaged packet is one logged as `Error submitting packet to decoder` or `corrupt decoded frame`, counting audio and video.

Allowed damage is set in the Options dialog as a percentage, 1% by default. It is read when a scan starts, so change it before scanning. 0 marks any damage invalid. A refresh keeps earlier results for unchanged files, so run a full scan to apply a new value to them.

Converting such a file gives a copy that decodes without errors. The glitch stays in the picture, because the lost data cannot be recovered. Getting a fresh copy from the source is the only way to make it perfect.

ffmpeg runs with `-loglevel repeat+level+info`, so repeated lines are not collapsed into "Last message repeated" and every damaged packet is counted.

Audio-only files have no video frames to compare against, so any damage in them stays invalid.

## Invalid (red)

- **Ends early.** The decoded length falls short of the duration in the file header by more than 1 second or 2%, whichever is larger. A truncated file often plays fine but stops early, and converting cannot bring back the missing part.
- **Too much damage.** More damaged packets than the allowed damage.
- **Other errors.** Any error that is not ignored, minor, or part of small damage. This includes container errors with no damaged packets, such as a garbled MKV.
- **ffmpeg fails.** A non-zero exit code.

## Known limits

- A demuxer that skips past a large hole in a file (most likely MKV) drops the packets in that hole without counting them as damaged. If the file still decodes to full length, it could be marked yellow when it should be red. Comparing the frame count against duration times frame rate would catch this.
- Minor ASF errors have no count limit, so a file with hundreds of them would still be yellow.
- The 1% default is a judgment call. Raise allowed damage in Options if files that play fine still come up red.
