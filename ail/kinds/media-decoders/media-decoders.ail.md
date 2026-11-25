# Media decoders

The decoders each platform uses for sound and video, how they are found, and what a decoder that fails leaves
behind. Windows and macOS are described from the code; only Linux was driven.

```ail
node MediaDecoders : fn   level: system
  why   Valafind ships no codecs: it reads media with what the computer already has, and must behave the same
        whichever decoder answers, never stopping indexing over a damaged file.   <- KindsOfFiles
  what
    type AvInfo = { length: duration, hasAudio: boolean, hasVideo: boolean, width: integer, height: integer }
    type AvBackend = { name: text, probe(path: text) -> AvInfo or nothing, decodeAudio(path: text, start: duration, length: duration) -> list of number or nothing, decodeFrames(path: text, times: list of duration, width: integer, height: integer) -> list of DecodedImage or nothing }
    MEDIA_TIMEOUT: duration = 60 s — per file and call, for pictures, sound and frames alike
    backends() -> list of AvBackend — probed once, best first:
      1. Windows 7 or later: Media Foundation, through its COM interfaces
      2. macOS 10.15 or later: AVFoundation, through the Objective-C runtime (also ImageIO for HEIC, HEIF, TIFF, AVIF, raw)
      3. any platform: ffmpeg and ffprobe, when both are found
    findCommand(name: text) -> text or nothing — the first file of that name (".exe" added on Windows) in the folders
      of PATH that are absolute paths, then, except on Windows, /usr/bin, /usr/local/bin, /opt/homebrew/bin,
      /opt/local/bin and /snap/bin
    ffmpeg: each probe and each clip or frame is a short-lived process with one thread, at the indexer's low priority:
      probe "ffprobe -v error -print_format json -show_format -show_streams -- <path>"; sound "-ss <start> -t <length>
      -i <path> -vn -ac 1 -ar 16000 -f f32le"; a frame "-ss <time> -i <path> -an -frames:v 1 -vf scale=<w>:<h>:flags=
      bicubic -f rawvideo -pix_fmt rgb24"; output is capped to what is expected; a failing process => nothing
    parseProbe(json: bytes) -> AvInfo or nothing — length from format.duration; a video stream with
      disposition.attached_pic (cover art) is not a picture track; a rotation of ±90° swaps width and height
    a failure of any kind (except running out of memory), or MEDIA_TIMEOUT => nothing for that file: it keeps its
      name vector, and the log says "Decoding <path> took longer than 00:01:00; indexing it by name only" for a timeout
    examples
      parseProbe(a 1920 × 1080 video stream with rotation −90 and an audio stream, duration "12.5") => { length: 12.5 s, hasAudio: true, hasVideo: true, width: 1080, height: 1920 }
      parseProbe(an audio stream and a 600 × 600 attached picture, duration "200.0") => { length: 200 s, hasAudio: true, hasVideo: false, width: 0, height: 0 }
      parseProbe("not json") => nothing
      backends() on this Linux machine with /usr/bin/ffmpeg and /usr/bin/ffprobe => [ffmpeg]
      backends() on Linux without ffmpeg => []: only WAV and the picture formats are decoded
  how
    decide the system's own decoders, ffmpeg only where there are none (Linux) or as a fallback
      because Valafind ships no codecs, and every Windows and Mac has these (PR #9, "Decoding")
    decide raw COM and Objective-C calls rather than platform target frameworks
      because the app then stays one build per operating system without Windows or macOS frameworks (PR #9)
    decide never run a program from a relative folder in PATH
      because a program planted in the current folder must never run (FfmpegBackend.ExecutableFinder)
    decide a timeout of one minute per decode
      because "unknown, confirm with the owner: why 60 s?"
  sources
    src/Valafind.Core/Extraction/Media/MediaDecoder.cs (DefaultBackends, Guard), IAvBackend.cs, FfmpegBackend.cs, MediaFoundationBackend.cs, AvFoundationBackend.cs
    tests/Valafind.Core.Tests/Extraction/MediaDecoderTests.cs, Ffprobe_output_is_parsed_with_rotation_and_cover_art
    PR #9 "… search pictures, audio and video by content" (Decoding; Impact: on Linux, audio other than WAV and all video need ffmpeg; the native paths were first run in CI)
    not driven: Media Foundation (Windows) and AVFoundation and ImageIO (macOS); only ffmpeg on Linux
```
