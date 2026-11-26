# Pictures, video and sound in the text model

A picture, a video or a clip of sound becomes one vector the same way text does: an encoder turns it into "soft
tokens", and the text model reads them in place of placeholder tokens, laid out exactly as the reference processor
lays them out.

```ail
node MediaInputs : fn   level: specialist
  why   Only the reference layout puts a picture, a recording and a question into one space, so that "a red circle"
        finds the picture of one.   <- EmbeddingModel
  what
    special token ids: BOS 2, EOS 1, BEGIN_IMAGE 255999, BEGIN_AUDIO 256000, IMAGE 258880, AUDIO 258881, END_IMAGE 258882,
      END_AUDIO 258883, VIDEO 258884
    FEATURE_WIDTH: integer = 512 — one soft token is 512 numbers; the text model's media inputs set it when it loads
    the vision encoder takes pixel_values [1 × patches × 768] and pixel_position_ids [1 × patches × 2] and gives
      [soft tokens × 512]; the audio encoder takes input_features [1 × frames × 128] and input_features_mask
      [1 × frames] and gives [soft tokens × 512]; each must have exactly these inputs and one output
    the text model takes input_ids and attention_mask (batch × length) and, when it declares them, image_features,
      video_features and audio_features [rows × 512]; the ones not in use are given 0 rows
    picture: ids = [BOS, BEGIN_IMAGE] + IMAGE × n + [END_IMAGE, EOS], image_features = the encoder's n soft tokens of
      preprocess(picture, 140)
    video: frames = selectFrames(count, 8); each frame is encoded on its own from preprocess(frame, 70);
      ids = [BOS] + for each frame [BEGIN_IMAGE] + VIDEO × its n + [END_IMAGE] + [EOS], video_features = all frames'
      soft tokens in order
    audio: ids = [BOS, BEGIN_AUDIO] + AUDIO × n + [END_AUDIO, EOS], audio_features = the encoder's n soft tokens of
      extractAudioFeatures(clip samples)
    no task prompt and no title for media; the vector is truncated and normalised like any other
    selectFrames(count: integer, max: integer) -> list of integer — every index when count ≤ max, otherwise
      for i from 0 to max − 1 the whole part of (count − 1) × i / (max − 1), and [0] when max is 1
    examples
      selectFrames(10, 8) => [0, 1, 2, 3, 5, 6, 7, 9]
      selectFrames(4, 8) => [0, 1, 2, 3]
      selectFrames(100, 1) => [0]
      the ids for red-circle.png (320 × 240) => 134 ids, starting [2, 255999, 258880, 258880], for 130 soft tokens
      clip.mp4 (4 frames) => 264 soft tokens, 66 per frame, with 70 soft tokens per frame
      speech.wav (100,098 samples) => 626 frames, 625 of them valid, 157 soft tokens
      an audio clip with no valid frame, or one the encoder turns into 0 soft tokens => refused as too short
    an indexed picture adds one content vector, an audio file one per clip (up to 4 clips of 30 s), a video one for its
      frames and one per clip of its sound (up to 2); each such chunk has empty text, and a clip's start and length
      are its position in whole seconds; the file also keeps the vector of its name
  how
    decide one encoder run per video frame
      because a search then waits for at most one frame, and memory stays small (PR #9)
    decide no padding of the patches to the full budget
      because the encoder returns the same soft tokens without it (VisionPreprocessor)
    decide the encoders load on first use and are closed when indexing goes idle
      because they hold hundreds of MB that a quiet app does not need (PR #9, releaseMediaResources)
  children  VisionPreprocessing, AudioFeatures
  sources
    PR #9 "Switch to EmbeddingGemma 2 and search pictures, audio and video by content"
    src/Valafind.Core/Embedding/EmbeddingGemmaModel.cs, EmbedImage, EmbedVideo, EmbedAudio, SelectFrames, RunTextModelOn
    src/Valafind.Core/Indexing/IndexingCoordinator.cs, EmbedMedia and MediaRecord; CoordinatorTuning (MaxAudioClips 4)
    tests/Valafind.Core.Tests/Embedding/MediaEmbeddingTests.cs; Fixtures/embeddinggemma-reference.json (images, audio, video)
    tests/Valafind.Core.Tests/EndToEnd/SemanticPipelineTests.cs, Finds_pictures_and_recordings_by_what_they_show_and_say
```
