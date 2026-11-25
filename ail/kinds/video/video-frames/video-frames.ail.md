# Video frames

Which frames of a video are taken, at what size, and which clips of its sound.

```ail
node VideoFrames : fn   level: system
  why   Every frame costs an encoder run; a few frames at the reference's one-per-second sampling stand for the
        video at a bounded cost.   <- Video
  what
    type VideoFrames = { frames: list of DecodedImage, timestamps: list of duration, length: duration }
    MAX_VIDEO_FRAMES: integer = 8 — between 1 and 32 when configured
    VIDEO_MAX_SIDE: integer = 1280
    MAX_VIDEO_AUDIO_CLIPS: integer = 2
    frameTimes(length: duration, maxFrames: integer) -> list of duration
      — n = the whole seconds of length, at least 1; every second 0 … n − 1 when n ≤ maxFrames, otherwise for i from
      0 to maxFrames − 1 the whole part of (n − 1) × i / (maxFrames − 1) seconds (just 0 when maxFrames is 1)
    fitSize(width: integer, height: integer, maxSide: integer) -> { width: integer, height: integer }
      — the video's own size when its longest side is at most maxSide; otherwise scaled to fit maxSide, aspect kept,
      each side rounded to an even number and at least 2; an unknown size (0 or less) gives maxSide by
      maxSide × 9 / 16 rounded down to an even number
    decodeVideo(path: text, maxFrames: integer, maxSide: integer) -> later VideoFrames or nothing
      1. each platform decoder is asked in turn; one that cannot probe the file, or finds no picture track, is passed
         over (a music file's cover art is not a picture track)
      2. the frames at frameTimes(its length, maxFrames) are decoded at fitSize(its size, maxSide), upright by the
         video's rotation; frames that fail are left out with their times; no frame at all => the next decoder
      3. no decoder gives a frame, any failure, or more than 60 s => nothing
    the indexer asks for MAX_VIDEO_FRAMES at VIDEO_MAX_SIDE and embeds all the frames as one vector, kept with the
      video's length in whole seconds; then its sound as up to MAX_VIDEO_AUDIO_CLIPS clips (see audio clips)
    examples
      frameTimes(0.4 s, 8) => [0 s]
      frameTimes(3.9 s, 8) => [0 s, 1 s, 2 s]
      frameTimes(10 min, 8) => [0 s, 85 s, 171 s, 256 s, 342 s, 427 s, 513 s, 599 s]
      fitSize(1920, 1080, 1280) => { width: 1280, height: 720 }
      fitSize(192, 108, 1280) => { width: 192, height: 108 }
      fitSize(192, 108, 96) => { width: 96, height: 54 }
      fitSize(0, 0, 1280) => { width: 1280, height: 720 }
      decodeVideo("clip.mp4" of 4 s at 192 × 108, 8, 1280) => 4 frames of 192 × 108 at [0 s, 1 s, 2 s, 3 s]
      decodeVideo("clip.mp4", 2, 96) => 2 frames of 96 × 54 at [0 s, 3 s]
      ffprobe of a 1920 × 1080 stream rotated by −90° => a picture track of 1080 × 1920
  how
    decide frames at whole seconds, like the reference's one frame per second
      because the vectors are checked against the Python reference (cosine above 0.98 through ffmpeg) (PR #9)
    decide on Windows a frame may come from up to half the gap to its neighbours (at most 2 s) before its time
      because decoding forward to the exact time made frame extraction 3 to 6 times slower (PR #17)
    decide even frame sizes
      because "unknown, confirm with the owner: why even sides?"
  uses      MediaDecoders, AudioClips
  sources
    src/Valafind.Core/Extraction/Media/MediaDecoder.cs (DecodeVideoAsync, FrameTimes, FitSize), FfmpegBackend.cs (ParseProbe)
    src/Valafind.Core/Embedding/EmbeddingOptions.cs (MaxVideoFrames 1 to 32); IndexingCoordinator.cs, CoordinatorTuning
    tests/Valafind.Core.Tests/Extraction/MediaDecoderTests.cs, FrameToleranceTests.cs
    PR #9 (video through ffmpeg, cosine > 0.98); PR #17 "Media Foundation frame sampling"
```
