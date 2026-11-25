# Pictures and video frames for the vision encoder

A port of the EmbeddingGemma 2 (Gemma 4) image and video processors.

```ail
node VisionPreprocessing : fn   level: specialist
  why   The vision encoder only sees what it was trained on when the pixels it gets match the reference processor's
        to within a couple of 8-bit levels.   <- MediaInputs
  what
    PATCH_SIZE: integer = 16;  POOLING_KERNEL: integer = 3;  PATCH_PIXELS: integer = 768 — 16 × 16 pixels × 3 colours
    SUPPORTED_SOFT_TOKENS: list of integer = [70, 140, 280, 560, 1120]
    type VisionPatches = { pixelValues: list of number, positionIds: list of integer, patches: integer }
    preprocess(image: DecodedImage, maxSoftTokens: integer) -> VisionPatches
      1. (height, width) = targetSize(image.height, image.width, maxSoftTokens × 9)
      2. resize to that size with bicubic antialiased resampling (resize)
      3. each 16 × 16 patch, left to right and top to bottom, row by row inside the patch, R, G, B each divided by 255:
         no mean or standard-deviation normalisation
      4. positionIds: (column, row) of each patch, in the same order
    targetSize(height: integer, width: integer, maxPatches: integer) -> { height: integer, width: integer }
      factor = √(maxPatches × 256 / (height × width))
      height' = ⌊factor × height / 48⌋ × 48;  width' = ⌊factor × width / 48⌋ × 48
      both 0 => refused (the picture cannot be put on the grid)
      height' 0 => height' = 48 and width' = min(⌊width / height⌋ × 48, maxPatches / 9 × 48), whole numbers
      width' 0 => width' = 48 and height' = min(⌊height / width⌋ × 48, maxPatches / 9 × 48)
    resize(rgb: bytes, width: integer, height: integer, newWidth: integer, newHeight: integer) -> bytes — Pillow's
      ImagingResample with the bicubic filter (a = −0.5, support 2, widened by the scale when shrinking): horizontal
      pass first, an 8-bit picture in between, then the vertical pass; weights in 22-bit fixed point, rounded and
      clamped to 0…255; the same size returns a copy
    examples
      targetSize(480, 640, 70 × 9) => { height: 336, width: 432 }
      targetSize(480, 640, 140 × 9) => { height: 480, width: 624 }
      targetSize(480, 640, 280 × 9) => { height: 672, width: 912 }
      targetSize(108, 192, 70 × 9) => { height: 288, width: 528 }
      targetSize(4000, 10, 280 × 9) => { height: 13440, width: 48 }
      preprocess(red-circle.png, 320 × 240, 140).patches => 1170
      preprocess(invoice.png, 400 × 200, 140).patches => 1152
      preprocess(rotated.jpg, upright 200 × 300, 140).patches => 1134
      resize(a flat colour (10, 200, 90) of 37 × 23, 37, 23, 101, 64) => 101 × 64 pixels, every one (10, 200, 90)
      resize(the same, 37, 23, 5, 3) => 5 × 3 pixels, every one (10, 200, 90)
    the picture may be scaled up: a small picture is enlarged to fill the patch budget
    pictures reach it decoded upright (EXIF orientation applied) and without alpha, at most 2048 pixels on their
      longer side; video frames at most 1280
  how
    decide port Pillow's resampler rather than use the platform's scaler
      because the reference (torchvision resize with antialias on 8-bit pixels) implements the same algorithm, so the
      pixels match within ±2 levels (BicubicResampler)
    decide sides are multiples of 48
      because the encoder pools 3 × 3 patches of 16 pixels into one soft token
  verify
    property: for every height, width ≥ 1 and supported budget with a result, both sides of targetSize are multiples of 48
    property: (height' / 16) × (width' / 16) ≤ maxPatches whenever neither side was 0 before the fix-up
    property: resize keeps a picture of one flat colour that colour, at any size
  sources
    PR #9 (aspect-preserving resize to the patch budget, Pillow's bicubic)
    src/Valafind.Core/Embedding/Media/VisionPreprocessor.cs, BicubicResampler.cs
    src/Valafind.Core/Indexing/IndexingCoordinator.cs, CoordinatorTuning (ImageMaxSide 2048, VideoMaxSide 1280)
    tests/Valafind.Core.Tests/Embedding/MediaEmbeddingTests.cs, Target_size_matches_the_reference_processor,
      Pictures_decode_and_patchify_like_the_reference, Bicubic_resampler_keeps_flat_colour_and_size
```
