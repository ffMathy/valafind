# Where the model files are found

The app never downloads the model. It looks for an EmbeddingGemma 2 export in a few fixed folders, and accepts a
folder only when its files say it really is that model.

```ail
node ModelFolder : data   level: system
  why   The app must find the model on its own, offline, and must never load the previous model by mistake: its
        vectors live in another space and would make every search wrong.   <- ModelAndIndex
  what
    MODEL_FOLDER_NAME: text = "embeddinggemma-2"
    LEGACY_MODEL_FOLDER_NAME: text = "embeddinggemma-300m" — the previous, text-only model; never loaded
    MODEL_TYPE: text = "embedding_gemma2" — the model_type a valid folder's config.json names
    findModelFolder() -> text or nothing — the first valid folder among the candidates, in this order:
      1. <data folder>/models/embeddinggemma-2
      2. <the app's own folder>/models/embeddinggemma-2
      3. on macOS only, <bundle>.app/Contents/Resources/models/embeddinggemma-2
    isModelFolder(directory: text) -> { valid: boolean, modelFile: text } — valid when the folder exists, holds
      tokenizer.json or tokenizer.model, has a config.json of at most 1 MiB whose model_type is MODEL_TYPE, and a usable
      text model; modelFile is the first usable one, relative, with forward slashes ("" when not valid)
    PREFERRED_MODEL_FILES: list of text = ["onnx/model_quantized.onnx", "onnx/model.onnx", "onnx/model_q4.onnx",
      "model_quantized.onnx", "model.onnx", "model_q4.onnx"]
    findVisionEncoder(directory: text) -> text or nothing — first usable of "onnx/vision_encoder_quantized.onnx",
      "onnx/vision_encoder.onnx", "onnx/vision_encoder_q4.onnx"
    findAudioEncoder(directory: text) -> text or nothing — likewise with audio_encoder
    a model file is usable when it is not fp16, exists, is not empty, and is at least 16 MiB or has its weights beside it
      in "<file>_data"; a name containing "fp16", "f16" or "half" (any case) is fp16
    examples
      isModelFolder("<missing folder>") => { valid: false, modelFile: "" }
      isModelFolder(folder with tokenizer.json, config.json of embedding_gemma2, onnx/model_quantized.onnx and its _data) => { valid: true, modelFile: "onnx/model_quantized.onnx" }
      isModelFolder(the same without onnx/model_quantized.onnx_data, the graph being small) => { valid: false, modelFile: "" }
      isModelFolder(folder with only onnx/model_fp16.onnx) => { valid: false, modelFile: "" }
      isModelFolder(folder with onnx/model_fp16.onnx and onnx/model_q4.onnx) => { valid: true, modelFile: "onnx/model_q4.onnx" }
      isModelFolder(folder with onnx/model_q4.onnx and onnx/model.onnx) => { valid: true, modelFile: "onnx/model.onnx" }
      isModelFolder(folder whose only model is model.onnx at its root) => { valid: true, modelFile: "model.onnx" }
      isModelFolder(an embeddinggemma-300m folder, no config.json or another model_type) => { valid: false, modelFile: "" }
      findVisionEncoder(a valid folder without encoders) => nothing
      findVisionEncoder(the same plus onnx/vision_encoder_quantized.onnx and its _data) => "onnx/vision_encoder_quantized.onnx"
    the encoders are optional: without them the model embeds text only, and pictures, audio and video are found by name
    the files a full model folder holds: tokenizer.json, tokenizer_config.json, config.json, processor_config.json,
      preprocessor_config.json, and in onnx/ model_quantized, vision_encoder_quantized and audio_encoder_quantized, each
      .onnx with its .onnx_data; about 850 MB in all
    they come from the Hugging Face repository onnx-community/embeddinggemma-2-ONNX (8-bit), fetched once by
      tools/fetch-model.sh or tools/fetch-model.ps1 into <data folder>/models/embeddinggemma-2, or carried in the app
  how
    decide accept a folder only when config.json names embedding_gemma2
      because the previous model's folder (embeddinggemma-300m) has other inputs and another vector space (PR #9)
    decide prefer 8-bit, then full precision, then 4-bit, and never fp16
      because EmbeddingGemma's activations overflow in fp16 (ModelLocator, EmbeddingOptions)
    decide no folder chosen by the user: only the data folder, the app's folder and the macOS bundle
      because "unknown, confirm with the owner: PR #31 removes the custom model location without saying why"
    decide a folder that cannot be read counts as "not here", never as an error
      because finding the model is a probe that runs again when the files change (ModelLocator.IsValid)
  children  BundledModel
  sources
    PR #9 (embedding_gemma2 check, models/embeddinggemma-2), PR #31 (custom location removed)
    src/Valafind.Core/Embedding/ModelLocator.cs; tools/fetch-model.sh; README.md, "Build from source"
    tests/Valafind.Core.Tests/Embedding/ModelLocatorTests.cs
```
