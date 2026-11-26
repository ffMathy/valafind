# Sound for the audio encoder

A port of the Gemma 4 audio feature extractor that EmbeddingGemma 2 uses: log-mel frames of 16 kHz mono sound.

```ail
node AudioFeatures : fn   level: specialist
  why   The audio encoder understands speech and sound only from the features it was trained on, so they must match
        the reference extractor to within 0.001.   <- MediaInputs
  what
    SAMPLE_RATE: integer = 16000;  MEL_BINS: integer = 128;  MAX_SAMPLES: integer = 480000 — 30 s
    type AudioFeatureFrames = { features: list of number, mask: list of boolean, frames: integer } — features is
      frames × 128, row by row
    extractAudioFeatures(samples: list of number) -> AudioFeatureFrames
      1. keep the first 480,000 samples; L = how many are left
      2. pad with zeros to a multiple of 128 samples, and put 160 zeros in front (semicausal): the waveform w
      3. frames = ⌊(length of w − 321) / 160⌋ + 1 (0 when w is shorter than 321); frame f starts at sample 160 × f
      4. mask[f] is true when sample 160 × f + 320 of w is real sound: at least 160 and below 160 + L
      5. each frame: 320 samples × a periodic Hann window, 0.5 − 0.5 cos(2πn / 320), zero-padded to 512; the magnitudes
         |FFT| of bins 0 to 256
      6. a frame whose mask is true: for each of 128 HTK mel filters from 0 to 8000 Hz, without area normalisation,
         log(Σ magnitude × filter + 0.001); a frame whose mask is false stays all 0
    HTK mel: mel(hz) = 2595 log₁₀(1 + hz / 700); the 130 filter edges are evenly spaced in mel from mel(0) to mel(8000);
      filter m at bin frequency f (8000 × k / 256 Hz) is max(0, min((f − edge m) / (edge m+1 − edge m),
      (edge m+2 − f) / (edge m+2 − edge m+1)))
    no pre-emphasis, no dither and no normalisation per bin: all are off in the model's configuration
    a clip comes as 16 kHz mono samples; decoding and resampling are the media decoder's
    examples
      extractAudioFeatures(40 s of silence).frames => 2999
      extractAudioFeatures(40 s of silence).features => every value log(0.001), about −6.9078
      the number of true values in extractAudioFeatures([]).mask => 0
      extractAudioFeatures(speech.wav, 100,098 samples) => 626 frames, 625 with a true mask
      the FFT of 64 random values => the direct DFT, to within 1e-9
  how
    decide a frame counts only when the last sample of its 321-sample window is real sound
      because that is how the reference extractor builds its mask
    decide frames are float32 samples times a float32 window, then summed in double precision
      because numpy computes them that way, which keeps the sums within 1e-4 of the reference
    decide an in-place radix-2 FFT of our own
      because 512 is a power of two and there is no FFT in the base library
  verify
    property: frames is ⌊(160 + ⌈min(L, 480000) / 128⌉ × 128 − 321) / 160⌋ + 1 for every L where that is at least 1
    property: a frame whose mask is false has only zeros
  sources
    PR #9 (log-mel features: 512-point FFT, 128 HTK mel bins, 30 s per clip)
    src/Valafind.Core/Embedding/Media/AudioFeatureExtractor.cs; src/Valafind.Core/Abstractions/Contracts.cs, AudioClip
    tests/Valafind.Core.Tests/Embedding/MediaEmbeddingTests.cs, Audio_features_match_the_reference_extractor,
      Audio_features_are_capped_at_thirty_seconds_and_handle_silence, Fft_matches_a_direct_dft
```
