# The model inside the app

Release builds carry the model files inside the executable and copy them out into the data folder on the first start.

```ail
node BundledModel : fn   level: system
  why   A single file to download, with nothing else to fetch, is what makes the app work out of the box and offline.
        <- ModelFolder
  what
    RESOURCE_PREFIX: text = "ValafindModel/" — the name of every embedded file starts with it; the rest is its path in
      the model folder, with forward slashes (backslashes from the build machine are turned into "/")
    type Bundle = { resourceNames: list of text, open(resourceName: text) -> bytes } — the files a build embeds in the app
    bundledFiles(bundle: Bundle) -> list of { relativePath: text, resourceName: text } — sorted by relativePath
      (ordinal); names with an empty, "." or ".." part are left out
    isBundled(bundle: Bundle) -> boolean — bundledFiles is not empty
    ensureInstalled(bundle: Bundle, targetDirectory: text) -> integer — writes every embedded file that is
      missing or whose size differs; returns how many were written
      each file is written to "<file>.part", sized up front, flushed to disk, then renamed over the final name;
        a failed write deletes the .part file and passes the error on (a full disk, a folder that cannot be written)
      folders are created private to the user (0700 on Unix), files 0600
      a file already there with the right size is not touched
    examples
      bundledFiles(a test bundle) => paths ["config.json", "onnx/model_quantized.onnx", "onnx/model_quantized.onnx_data", "tokenizer.json"]
      ensureInstalled(that bundle, an empty folder) => 4, and the folder is then a valid model folder with modelFile "onnx/model_quantized.onnx"
      ensureInstalled(that bundle, the same folder again) => 0, and tokenizer.json keeps its time stamp
      ensureInstalled(after deleting tokenizer.json and cutting onnx/model_quantized.onnx_data short) => 2
      ensureInstalled(a bundle without a model, any folder) => 0, and the folder is not created
    what a release build embeds, from -p:ValafindModelDir=<folder>: tokenizer.json, tokenizer_config.json, config.json,
      processor_config.json, preprocessor_config.json and the three 8-bit ONNX files with their .onnx_data; the build
      stops with "ValafindModelDir: missing model file …" when one is missing
    at start, before the model is looked for (installBundledModel):
      nothing bundled => nothing happens
      findModelFolder() finds a valid folder other than <data folder>/models/embeddinggemma-2 => nothing is unpacked
      otherwise => ensureInstalled into <data folder>/models/embeddinggemma-2, then the folder
        <data folder>/models/embeddinggemma-300m is deleted (if it is in use, it is tried again at the next start)
      a write error is logged, and when no model is found the launcher shows that the model could not be unpacked
  how
    decide embed the model in an assembly of its own (Valafind.Model)
      because the ~850 MB of weights should never pass through the main app's compile (Valafind.Model.csproj)
    decide unpack rather than read the model from inside the executable
      because ONNX Runtime loads files; bundle compression stays off, or the weights would be inflated into memory at
      every start (PR #2)
    decide compare sizes only, not contents
      because after the first start this costs a few file-size probes (BundledModel)
    decide skip unpacking when another valid model is in use
      because no disk space should go to an unused copy (CoreServicesFactory.InstallBundledModel)
  sources
    PR #2 "Bundle EmbeddingGemma in single-file binaries", PR #9 (300m folder removed)
    src/Valafind.Core/Embedding/BundledModel.cs; src/Valafind.Model/Valafind.Model.csproj; src/Valafind.Model/ModelResources.cs
    src/Valafind.App/Services/CoreServicesFactory.cs, InstallBundledModel and RemoveLegacyModel; AppHost.cs (ModelUnpackFailed)
    tests/Valafind.Core.Tests/Embedding/BundledModelTests.cs
    not driven: the checkout's build has no model embedded; run-legacy.sh links the model folder into the data folder
```
