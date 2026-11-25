# The embedding model

EmbeddingGemma 2 on ONNX Runtime: one text model that turns token sequences into one vector, and two encoders whose
output takes the place of placeholder tokens for pictures, video frames and sound.

```ail
node EmbeddingModel : fn   level: system
  why   Every search and every indexed file goes through the model, so its inputs, its output and its options must
        be exact: a vector made differently from the reference is a search that finds the wrong file.   <- ModelAndIndex
  what
    type EmbeddingDevice = "auto" | "cpu" | "gpu"
    type EmbeddingOptions = { modelDirectory: text, modelFile: text = "onnx/model_quantized.onnx", dimensions: integer = 768,
      maxTokens: integer = 512, device: EmbeddingDevice = "cpu", gpuCrashMarker: text or nothing, intraOpThreads: integer = 2,
      batchSize: integer = 8, enableMedia: boolean = true, imageSoftTokens: integer = 140, videoFrameSoftTokens: integer = 70,
      maxVideoFrames: integer = 8 }
    type DocumentText = { title: text, text: text }
    type DecodedImage = { width: integer, height: integer, rgb: bytes } — 8-bit R, G, B per pixel, row by row
    type AudioClip = { samples: list of number, offset: duration } — 16 kHz mono, at most 30 s
    EmbeddingModel(options: EmbeddingOptions) -> EmbeddingModel — checks the options and finds the files; loads nothing yet
    EmbeddingModel.modelId: text — "embeddinggemma-2/<model file name without .onnx>/<dimensions>"
    EmbeddingModel.dimensions: integer
    EmbeddingModel.maxInputTokens: integer — maxTokens − 44: the most tokens a chunk may have and still fit with its prompt
    EmbeddingModel.countTokens(text: text) -> integer — tokens of the text as it is: no prompt, no BOS or EOS; "" => 0
    EmbeddingModel.embedQuery(query: text) -> list of number — scheduled as interactive
    EmbeddingModel.embedDocuments(documents: list of DocumentText) -> list of list of number — in the caller's order
    EmbeddingModel.embedDocumentsInteractive(documents: list of DocumentText) -> list of list of number — the same, as interactive
    EmbeddingModel.embedImage(image: DecodedImage) -> list of number
    EmbeddingModel.embedVideo(frames: list of DecodedImage) -> list of number — at least one frame
    EmbeddingModel.embedAudio(clip: AudioClip) -> list of number
    EmbeddingModel.mediaCapabilities: set of "image" | "video" | "audio" — image and video with the vision encoder, audio with the audio encoder
    EmbeddingModel.ensureLoaded() -> nothing — loads the tokenizer and the text model now
    EmbeddingModel.releaseMediaResources() -> nothing — closes both encoders; the next picture or clip opens them again
    every vector is the model's sentence_embedding, its first `dimensions` values kept and scaled to length 1
    truncateAndNormalize(vector: list of number, dimensions: integer) -> list of number
    examples
      EmbeddingModel({ modelDirectory: m, dimensions: 256 }).modelId => "embeddinggemma-2/model_quantized/256"
      EmbeddingModel({ modelDirectory: m }).modelId => "embeddinggemma-2/model_quantized/768"
      EmbeddingModel({ modelDirectory: m, maxTokens: 512 }).maxInputTokens => 468
      EmbeddingModel({ modelDirectory: m, maxTokens: 80 }).maxInputTokens => 36
      truncateAndNormalize([3, 4, 12, 0], 2) => [0.6, 0.8]
      truncateAndNormalize([3, 4, 12, 0], 4) => [3 / 13, 4 / 13, 12 / 13, 0]
      truncateAndNormalize([0, 0, 0, 0, 0, 0, 0, 0], 4) => [0, 0, 0, 0]
      dimensions 64, 300, 1024 or 0 => refused; maxTokens 10 or 8193 => refused; batchSize 0 or intraOpThreads −1 => refused
      a model file named with fp16 => refused; the default file missing but another variant valid => that variant is used
      embedDocuments([]) => [];  embedDocuments([{ title: "empty.txt", text: "" }]) => one vector of length 1
      embedQuery("") => a vector of 768 values
      embedVideo([]) => refused;  embedAudio(a clip too short to give one frame) => refused
    allowed values: dimensions 768, 512, 256 or 128; maxTokens 64 to 8192; soft tokens 70, 140, 280, 560 or 1120;
      maxVideoFrames 1 to 32
    the app uses { dimensions: the setting EmbeddingDimensions (768), device: "auto" when the setting UseGpu is on
      (it is by default) and "cpu" when off, gpuCrashMarker: <data folder>/gpu-check.pending }, loads the model at
      start and embeds the query "warm up" once
  how
    decide load on first use, or at start by ensureLoaded; a failed load is not remembered, so the next call tries again
      because a model installed while the app runs must be picked up without a restart (README, PR #1)
    decide documents are sorted by length into batches of batchSize, and put back in the caller's order
      because each batch is padded to its longest input; padding changes no vector (cosine above 0.9999)
    decide Matryoshka truncation then re-normalisation; a zero vector stays zero, never NaN
      because EmbeddingGemma is trained so that its first 512, 256 or 128 values are a meaning of their own
    decide pictures 140 soft tokens, video frames 70, at most 8 frames per video
      because the model's defaults (280, 140 and 32) cost too much CPU in the background: 2.8 s instead of 6.4 s a
      picture on two threads (PR #9)
    decide ONNX Runtime threads never spin while idle (allow_spinning 0), with 2 threads inside an operation
      because spinning burns CPU that the indexing throttle cannot see (EmbeddingGemmaModel.OpenSession)
  free      [ how loading is locked, how sessions are kept ]
  children  EmbeddingInputs, Tokenizer, MediaInputs, RunScheduler, ComputeDevice, ReferenceParity
  sources
    PR #1, PR #9, PR #21; README.md, "Development"
    src/Valafind.Core/Embedding/EmbeddingGemmaModel.cs, EmbeddingOptions.cs, Matryoshka.cs
    src/Valafind.Core/Abstractions/Contracts.cs; src/Valafind.App/Services/CoreServicesFactory.cs, Create
    tests/Valafind.Core.Tests/Embedding/EmbeddingUnitTests.cs, EmbeddingGemmaModelTests.cs, ModelLocatorTests.cs
```
