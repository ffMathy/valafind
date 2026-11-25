# Audio

A recording is found by what is said or heard in it: up to four 30-second clips of it are listened to, spread over
the whole of a long recording.

```ail
node Audio : flow   level: experience
  why   Voice memos and recorded meetings are named by date or number; people remember what was said ("the meeting
        about the budget").   <- KindsOfFiles
  what
    WAV is read on every platform without any codec. Other recordings (MP3, AAC and M4A, FLAC, Ogg, Opus, WMA, AIFF)
      are read with the codecs the system has: Media Foundation on Windows, AVFoundation on a Mac, and an installed
      ffmpeg on Linux (or anywhere, when the system's codecs cannot read a file). Without ffmpeg on Linux only WAV is
      listened to; a recording no codec can read is found by its name.
    A recording of up to two minutes is listened to in 30-second clips from its start to its end. A longer one is
      listened to in four 30-second clips spread evenly over it: the first at its start, the last ending at its end.
    A clip shorter than half a second is not listened to; a recording that is all too short, has no sound track, or
      cannot be decoded within a minute is found by its name only.
    A result for a recording shows the audio icon and its details, without an excerpt.
    Scenario: a recording is found by what is said in it
      Given a WAV recording in which a weather forecast is read out, and a WAV of a steady tone, with names that mean
        nothing
      When the user searches for "a weather forecast"
      Then the forecast is listed above the tone
    Scenario: MP3 and FLAC recordings are read too
      Given the same forecast saved as MP3 and as FLAC, and a WAV of a steady tone
      When the user searches for "a weather forecast"
      Then both forecasts are listed above the tone
    Scenario: a long recording is heard to its end
      Given a WAV recording of 150 seconds of silence followed by the forecast, and a WAV of a steady tone
      When the user searches for "a weather forecast"
      Then the long recording is listed above the tone
  how
    decide listen to clips of at most 30 seconds, up to four per recording
      because 30 seconds is what the model's audio encoder takes at a time, and four keep a long file's cost bounded
      (PR #9, "audio: up to 4 × 30 s clips, spread over the file when it is long")
    decide spread the clips evenly over a long recording, first at the start and last at the end
      because a long recording should be represented from beginning to end (MediaDecoder)
    decide WAV is read directly, everything else through the platform's codecs and ffmpeg on Linux
      because WAV then sounds the same to the model on every platform, and Valafind ships no codecs (PR #9; FfmpegBackend)
    decide nothing between the clips of a long recording is listened to
      because "unknown, confirm with the owner: words said between the four clips of a long recording cannot be found; is that acceptable?"
  look
    a recording's result row shows the pink audio icon (a note) with its name, folder and date, and no excerpt
  children  AudioClips
  sources
    README.md, "What gets indexed": Audio (WAV everywhere; MP3, AAC/M4A, FLAC, … through the system's codecs)
    PR #9 "Switch to EmbeddingGemma 2 …" (Indexing, Decoding, Impact: on Linux, audio other than WAV needs ffmpeg)
    driven on Linux (Xvfb, openbox) 2026-11-25: the speech fixture as WAV, MP3, FLAC and 150 s of silence plus speech, and a 440 Hz tone, made with ffmpeg
    src/Valafind.Core/Extraction/Media/MediaDecoder.cs (DecodeAudioAsync, ClipWindows), WavReader.cs, FfmpegBackend.cs
    src/Valafind.Core/Indexing/IndexingCoordinator.cs, EmbedMedia (MaxAudioClips 4)
```
