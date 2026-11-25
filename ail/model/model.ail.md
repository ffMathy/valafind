# The model, embeddings and the index

What turns files and questions into meanings that can be compared, and where those meanings are kept: EmbeddingGemma 2
on the user's own computer, the tokenizer, chunking, picture and sound preprocessing, the graphics card or the
processor, and the index database that a search scores against. Almost everything here is invisible: the user meets
it as results that match what they meant, and as one line in the log.

```
model.ail.md                                                        ModelAndIndex (this file)
model-folder/model-folder.ail.md                                    ModelFolder: where the model files are found
model-folder/bundled-model/bundled-model.ail.md                     BundledModel: unpacking the model from the app
embedding-model/embedding-model.ail.md                              EmbeddingModel: text, pictures and sound to vectors
embedding-model/embedding-inputs/embedding-inputs.ail.md            EmbeddingInputs: prompts, titles, truncation
embedding-model/tokenizer/tokenizer.ail.md                          Tokenizer: tokenizer.json, exactly as the reference
embedding-model/media-inputs/media-inputs.ail.md                    MediaInputs: soft tokens in the text model's input
embedding-model/media-inputs/vision-preprocessing/…                 VisionPreprocessing: resize and patches
embedding-model/media-inputs/audio-features/…                       AudioFeatures: log-mel frames
embedding-model/run-scheduler/run-scheduler.ail.md                  RunScheduler: a search never waits for indexing
embedding-model/compute-device/compute-device.ail.md                ComputeDevice: WebGPU or the CPU, the ready line
embedding-model/reference-parity/reference-parity.ail.md            ReferenceParity: agreeing with the Python reference
text-chunking/text-chunking.ail.md                                  TextChunking: a document into overlapping chunks
index-database/index-database.ail.md                                IndexDatabase: index.db, its tables and meta
index-database/path-keys/path-keys.ail.md                           PathKeys: one row per file, per platform
vector-search/vector-search.ail.md                                  VectorSearch: how a question is scored
```

```ail
node ModelAndIndex : library   level: experience
  why   Searching by meaning needs a model that understands text, pictures and sound alike, and it must run on the
        user's own computer, because their files and questions are private.   <- Valafind
  what
    The model is EmbeddingGemma 2, built into the app; nothing is downloaded and nothing leaves the computer.
    Text, pictures, audio and video are put into one space of meanings, so a question in words finds a picture
      by what it shows and a recording by what is said.
    Questions and documents may be in different languages: an English question finds a Danish lease.
    The model runs on the graphics card when one is usable and on the processor otherwise; the log says which
      once the app is ready.
    A search started while indexing is busy goes first; indexing picks up where it was interrupted.
    The index keeps a short excerpt of each part of a document next to its meaning, only for the user.
    When the model or the size of its meanings changes, the index is built again from scratch.
    Once the model has loaded, the log gets one line saying how long it took, which model and size it is, and where
      it runs; when the graphics card was not used, the reason follows in brackets.
  how
    decide one model for every kind of file, in one space
      because a question must be compared with documents, pictures and sound alike (PR #9)
    decide the model ships inside the app
      because there should be nothing else to download, and the app works offline (PR #2, README)
    decide the full 768-sized meanings by default
      because "unknown, confirm with the owner: PR #21 changes the default from 256 to 768 without saying why"
  copy
    log.ready   "Core services ready in {milliseconds} ms (model {model} on {device})"
    log.device  "CPU" | "GPU: {adapter} (WebGPU)", followed by " ({why the graphics card was not used})" when it was not
  children  ModelFolder, EmbeddingModel, TextChunking, IndexDatabase, VectorSearch
  sources
    README.md, "Nothing leaves your computer", "What gets indexed" and "Where your data lives"
    PR #1 (first version), PR #2 (bundled model), PR #9 (EmbeddingGemma 2 and media), PR #17 (GPU), PR #21 (768), PR #31
    driven on Linux (Xvfb, openbox) 2026-11-25, state .ail/model: the log line "Core services ready in 2366 ms (model
      embeddinggemma-2/model_quantized/768 on CPU (no WebGPU adapter found (GPU driver, or on Linux the Vulkan loader,
      missing)))"; a saved text file became one row in index.db with its excerpt
    src/Valafind.App/Services/CoreServicesFactory.cs; src/Valafind.App/Services/AppHost.cs; src/Valafind.App/Services/Log.cs
```
