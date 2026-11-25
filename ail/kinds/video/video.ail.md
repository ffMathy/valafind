# Video

A video is found by what its pictures show, from eight frames spread over it, and by what is said or heard in it,
from up to two 30-second clips of its sound.

```ail
node Video : flow   level: experience
  why   Home videos and screen recordings carry names like "VID_20240612.mp4"; people remember what happened in them
        or what was said.   <- KindsOfFiles
  what
    MP4, MOV, MKV, AVI, WebM, M4V, WMV and 3GP are read with the codecs the system has: Media Foundation on Windows,
      AVFoundation on a Mac, and an installed ffmpeg on Linux (or anywhere, when the system's codecs cannot read a
      file). Without ffmpeg on Linux a video is found by its name only.
    Its pictures: one frame per second of the video, thinned out evenly to at most eight, from its first second to its
      last, each made at most 1,280 pixels on its longest side; together they give one meaning.
    Its sound: up to two 30-second clips, from the start when they cover it all, otherwise one at the start and one
      ending at the end; a video without sound is found by its pictures alone.
    A video whose frames and sound cannot be decoded within a minute each is found by its name only.
    A result for a video shows the video icon and its details, without an excerpt.
    Scenario: a video is found by what its pictures show
      Given a 3-second video of a red circle and one of a blue square, without sound, with names that mean nothing
      When the user searches for "a red circle"
      Then the video of the red circle is listed above the other
    Scenario: a video is found by what is said in it
      Given a black video in which a weather forecast is read out, and a black video with a steady tone
      When the user searches for "a weather forecast"
      Then the video with the forecast is listed above the other
  how
    decide eight frames per video
      because the model's 32 would cost four times as much processor time per video; 8 is a default chosen for the
      processor's cost (PR #9, "Defaults chosen for CPU cost")
    decide one frame per second, thinned out evenly
      because that is how the model's reference samples video (PR #9, "video: one frame per second, thinned out evenly")
    decide two clips of sound for a video, four for an audio file
      because "unknown, confirm with the owner: why two clips for a video's sound?"
    decide video comes last of all kinds
      because it is the slowest work (README, "Stays out of your way")
  look
    a video's result row shows the pink video icon (a camera) with its name, folder and date, and no excerpt
  children  VideoFrames
  sources
    README.md, "What gets indexed": Video, eight frames spread over the video, plus up to two 30-second clips of its sound
    PR #9 "Switch to EmbeddingGemma 2 …" (8 frames instead of 32; 70 tokens per frame); PR #17 (Media Foundation frame sampling)
    driven on Linux (Xvfb, openbox) 2026-11-25: videos of a red circle, a blue square, the speech fixture over black and a tone over black, made with ffmpeg
    src/Valafind.Core/Extraction/Media/MediaDecoder.cs (DecodeVideoAsync, FrameTimes, FitSize)
    src/Valafind.Core/Indexing/IndexingCoordinator.cs, EmbedMedia (MaxVideoFrames 8, VideoMaxSide 1280, MaxVideoAudioClips 2)
```
