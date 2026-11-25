# Audio clips

Which parts of a recording are listened to, and how they are decoded into the model's 16 kHz mono samples.

```ail
node AudioClips : fn   level: system
  why   The audio encoder takes 30 seconds at a time, and every clip costs about 7 s of the processor's time; the
        clips chosen must stand for the whole recording at a bounded cost.   <- Audio
  what
    type AudioClip = { samples: list of number, offset: duration } — mono, at SAMPLE_RATE, at most MAX_SECONDS long
    SAMPLE_RATE: integer = 16000
    MAX_SECONDS: integer = 30
    MIN_CLIP_LENGTH: duration = 0.5 s
    MAX_AUDIO_CLIPS: integer = 4 — for an audio file; a video's sound gets 2 (see video frames)
    clipWindows(length: duration, maxClips: integer) -> list of { start: duration, length: duration }
      1. length 0 or less => []
      2. length at most maxClips × 30 s: consecutive windows of 30 s from 0, the last one shorter; a last window under
         MIN_CLIP_LENGTH is dropped unless it is the first
      3. otherwise maxClips windows of 30 s, the i-th starting at the whole seconds of (length − 30 s) × i / (maxClips − 1)
         (at 0 when maxClips is 1)
    decodeAudio(path: text, maxClips: integer) -> later list of AudioClip or nothing
      1. a WAV file (PCM of 8, 16, 24 or 32 bits, or float of 32 or 64 bits, also in the extensible format) is read
         directly: channels averaged to mono, resampled to SAMPLE_RATE with a windowed-sinc filter
      2. otherwise each platform decoder is asked in turn (see media decoders); the first that can probe the file
         decides: no sound track => []; else its windows decoded at SAMPLE_RATE mono, each cut to MAX_SECONDS
      3. clips shorter than MIN_CLIP_LENGTH are dropped; no decoder, or no clip from any => nothing
      4. any failure, or more than 60 s for the whole file => nothing
    the indexer embeds each clip as one vector, kept with the clip's start and length in whole seconds
    examples
      clipWindows(10 s, 4) => [{ start: 0 s, length: 10 s }]
      clipWindows(70 s, 4) => [{ start: 0 s, length: 30 s }, { start: 30 s, length: 30 s }, { start: 60 s, length: 10 s }]
      clipWindows(120 s, 4) => four windows of 30 s at 0, 30, 60 and 90 s
      clipWindows(156.26 s, 4) => four windows of 30 s at 0, 42, 84 and 126 s
      clipWindows(1 h, 4) => four windows of 30 s at 0, 1190, 2380 and 3570 s
      clipWindows(0 s, 4) => []
      decodeAudio("speech.wav", 4) => one clip at offset 0 s with between 5 × 16,000 and 8 × 16,000 samples
      decodeAudio(a stereo WAV of 2 s at 44.1 kHz, 4) => one mono clip of 31,900 to 32,000 samples
      decodeAudio("bogus.mp3" holding text, 4) => nothing
      decodeAudio("clip.mp4", 2) => one clip of 3 × 16,000 to 5 × 16,000 samples
  how
    decide clips from the start when they cover the whole file, else spread with the last ending at the end
      because a long recording should be represented from beginning to end (MediaDecoder.ClipWindows)
    decide resampling weights are computed once per phase
      because evaluating them for every tap cost several seconds per 30-second clip; one file's decoding went from
      15.5 s to 1.3 s (PR #17)
    decide audio and video files are decoded one at a time
      because the platform decoders hold large buffers and some are not known to be safe to use concurrently
      (IndexingCoordinator, code comment)
  uses      MediaDecoders
  sources
    src/Valafind.Core/Extraction/Media/MediaDecoder.cs (DecodeAudioAsync, ClipWindows, Trim), WavReader.cs, AudioResampler.cs
    src/Valafind.Core/Abstractions/Contracts.cs, AudioClip (SampleRate, MaxSeconds)
    src/Valafind.Core/Indexing/IndexingCoordinator.cs, EmbedMedia and AddClips (one decode at a time under a lock)
    tests/Valafind.Core.Tests/Extraction/MediaDecoderTests.cs, AudioResamplerTests.cs
    PR #9, PR #17 "Index ~9x faster: … faster audio resampling"
```
