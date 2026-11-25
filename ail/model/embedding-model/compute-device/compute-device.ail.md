# The graphics card or the processor

The model runs on the GPU through ONNX Runtime's WebGPU provider when one is usable, and on the CPU otherwise. The
log's "Core services ready" line says which.

```ail
node ComputeDevice : fn   level: system
  why   On a graphics card indexing is 10 to 20 times faster (about 70 ms instead of a second per text chunk), but no
        user should have to install CUDA or any other toolkit, and a broken driver must never stop the app.   <- EmbeddingModel
  what
    choosing the text model's device when it loads:
      device "cpu" => the CPU; the GPU is never probed and no marker is written
      device "auto" and the crash marker holds this build's stamp => the CPU, with the reason "Valafind stopped
        unexpectedly while first using the GPU; staying on the CPU (delete '<marker>' to retry)"; the marker is left
      otherwise the WebGPU device, found once per process: the provider library registered with ONNX Runtime, then the
        first WebGPU device it lists, with power preference "high-performance"
      no device => "auto" uses the CPU with the reason; "gpu" fails with "No usable GPU: <reason>"
      a GPU session is opened, checked, and run once on [BOS, EOS] with the marker file in place; a failure there makes
        "auto" fall back to the CPU with the reason "GPU session failed: <message>"
    each encoder is tried on the GPU when the text model is there, and checked with one run (a black 64 × 64 picture,
      one second of silence); an encoder that fails stays on the CPU while the text model keeps the GPU
    the marker: <data folder>/gpu-check.pending, holding "<Valafind version> webgpu <provider version>"; it exists only
      during a first GPU run; one left by another build is ignored, and the GPU is tried again
    nodes kept on the CPU on every GPU: every GatherBlockQuantized (the text model's 8-bit token lookup) and the
      Unsqueeze node named node_unsqueeze_1 (in the audio encoder); their names are read from the ONNX graph itself
    activeDevice: text — "CPU", or "GPU: <adapter description, else vendor> (WebGPU)"
    the log line, once the model is loaded and the index opened:
      "Core services ready in <ms> ms (model <modelId> on <activeDevice>[ (<reason the GPU was not used>)])"
    examples
      a Linux machine without a Vulkan loader, setting UseGpu on => "Core services ready in 2366 ms (model embeddinggemma-2/model_quantized/768 on CPU (no WebGPU adapter found (GPU driver, or on Linux the Vulkan loader, missing)))"
      device "cpu" => activeDevice "CPU", no reason, and no gpu-check.pending is created
      device "auto" with a marker of this build => activeDevice "CPU", the reason names the marker, the marker stays
      device "auto" with a marker "0.0.0.0 webgpu 0.0.0.0" on a machine with a GPU => activeDevice starts with "GPU" and the marker is removed
      activeDevice on an NVIDIA GeForce GTX 1070 => "GPU: NVIDIA GeForce GTX 1070 (WebGPU)"
      cpuNodes(the text model's graph) => ["/model/embed_tokens/Gather_Quant"]
      cpuNodes(the audio encoder's graph) => ["node_unsqueeze_1"]
      nodeNames(a graph with nodes node_unsqueeze and node_unsqueeze_1, "Unsqueeze") => ["node_unsqueeze", "node_unsqueeze_1"]
      nodeNames(bytes that are not a protobuf message, "Unsqueeze") => refused as malformed
    the WebGPU back ends: Direct3D 12 on Windows, Vulkan on Linux, Metal on Apple silicon
  how
    decide WebGPU, not CUDA or DirectML
      because it needs no vendor toolkit and covers every platform (PR #17, README)
    decide keep the provider at 0.5.0
      because 0.6.0 aborts the process when a session is created (PR #17)
    decide a marker file around the first GPU run
      because some GPU kernels abort the whole process instead of failing, which nothing in it can catch (PR #17)
    decide name the nodes that crash, found by bisecting on a GTX 1070, and keep them on the CPU
      because those kernels abort the process on D3D12 and Vulkan alike; moving every Unsqueeze to the CPU aborts again
    decide read node names from the graph with a small protobuf reader of our own
      because no ONNX library is needed for it, and the weights in .onnx_data are never read (OnnxGraph)
    decide the settings.json key UseGpu, with no switch in Settings
      because "unknown, confirm with the owner: PR #17 says a switch would need 20 translations; keep it file-only?"
  sources
    PR #17 "Index ~9x faster: GPU inference …"; README.md, "What gets indexed" (the GPU paragraph)
    src/Valafind.Core/Embedding/WebGpuDevice.cs, OnnxGraph.cs; EmbeddingGemmaModel.cs, GetSession, VerifyOnGpu, GetMediaSession
    src/Valafind.App/Services/CoreServicesFactory.cs; AppHost.cs; Log.cs (CoreReady)
    tests/Valafind.Core.Tests/Embedding/GpuEmbeddingTests.cs, OnnxGraphTests.cs
    driven on Linux (Xvfb, openbox) 2026-11-25: the CPU line above in .ail/model/valafind.log; no GPU here, so the
      GPU path was not driven
```
