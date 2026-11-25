# Agreeing with the reference

Valafind's own tokenizer, preprocessing and model calls are checked against a committed reference made by the
official Python pipeline on the same ONNX files.

```ail
node ReferenceParity : fn   level: specialist
  why   The whole pipeline is a port; the only proof that it means what the model means is that its numbers agree
        with the reference's, and stay so when either side changes.   <- EmbeddingModel
  what
    the reference: tests/Valafind.Core.Tests/Fixtures/embeddinggemma-reference.json, written by
      "python tools/make-embedding-reference.py <model folder> <output.json>" with the Hugging Face transformers
      processor (5.23 or later) and Python onnxruntime on onnx/model_quantized, vision_encoder_quantized and
      audio_encoder_quantized, at Valafind's own settings: 140 soft tokens per picture, 70 per video frame
    it holds, for 7 texts (one query prompt, five document prompts, one raw text) their token ids and 768-value
      embeddings; the four README similarities; for red-circle.png, invoice.png and rotated.jpg their size, patches,
      soft tokens, first pixel values, pixel sum and embedding; for speech.wav its samples, frames, valid frames,
      first features, feature sum, soft tokens and embedding; for clip.mp4 its frames, soft tokens and embedding; four
      text queries and their embeddings
    the rules, each a test that runs when VALAFIND_TEST_MODEL_DIR names a model folder (the GPU ones also need
      VALAFIND_TEST_GPU=1) and is skipped otherwise:
      token ids                         equal, every id, with BOS and EOS
      text embedding at 768             cosine > 0.999 with the reference, raw and through the prompts
      Matryoshka 256                    cosine > 0.999 with the reference truncated and normalised
      a batch against one at a time     cosine > 0.9999 (also at 256 values, 80 tokens, batches of 3)
      the README ranking                the same order as the reference, Mars first, each similarity within 0.005;
                                        Mars still first at 256 values
      picture pixels                    each of the first values within 2.5 / 255; the sum within 0.2 %
      picture embedding                 length 1 to 4 decimals, cosine > 0.995
      audio features                    each of the first values within 0.001; the sum within 0.01 %
      audio embedding                   cosine > 0.999
      video embedding                   cosine > 0.98, through this machine's decoder (frames may differ by a level)
      GPU against CPU                   cosine > 0.9999 for text, a query, a picture, a video and a clip
      the same query, 6 at once         cosine > 0.99999 with the query alone
    examples
      the README similarities of "Which planet is known as the Red Planet?" to Venus, Mars, Jupiter, Saturn => [0.6754, 0.8535, 0.7473, 0.7773]
      the best match of "a red circle" among red-circle.png, invoice.png and speech.wav => red-circle.png
      the best match of "invoice" among them => invoice.png
      the best match of "weather forecast" among them => speech.wav
      end to end, files named scan_0042.docx (a Danish lease), n1.md (sourdough), q3.txt (churn), retry.py: the first result of "the lease agreement where we negotiated the deposit" => scan_0042.docx
      the first result of "how do I make sourdough bread" => n1.md;  of "why are customers cancelling their subscriptions" => q3.txt
      the first result of "function that retries with exponential backoff" => retry.py
      end to end with IMG_0001.png (red circle), IMG_0002.png (invoice), rec_17.wav (speech) and n.txt (shopping list) => 4 files, 7 chunks
      the first result there of "groceries to buy" => n.txt;  of "weather forecast for tomorrow" => rec_17.wav
  how
    decide reference vectors are committed, not computed by the tests
      because the tests then need no Python, and a changed reference shows in review
    decide exact ids, but cosine thresholds for vectors
      because 8-bit kernels and float sums differ slightly between runtimes; a token id cannot be "close"
    decide a looser bound for pictures (0.995) and video (0.98) than for text and audio (0.999)
      because the resampler matches within ±2 levels and H.264 decoders differ by a level here and there
  verify
    property: after tools/make-embedding-reference.py is re-run on the same model folder, every rule above still holds
  sources
    PR #1 (parity job), PR #9 (media parity, thresholds), PR #17 (GPU parity); README.md, "Development"
    tools/make-embedding-reference.py; tests/Valafind.Core.Tests/Embedding/ModelFixture.cs, EmbeddingGemmaModelTests.cs,
      MediaEmbeddingTests.cs, GpuEmbeddingTests.cs; tests/Valafind.Core.Tests/EndToEnd/SemanticPipelineTests.cs
```
