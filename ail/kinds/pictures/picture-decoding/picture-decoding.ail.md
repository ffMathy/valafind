# Decoding a picture

Which pictures are decoded where, and the pixels the model is given: upright, without transparency, and no larger
than it needs.

```ail
node PictureDecoding : fn   level: system
  why   The model must see a picture the way a person does (upright, whole) and the way the reference loader did, and
        a hostile or huge picture must not cost unbounded memory.   <- Pictures
  what
    type DecodedImage = { width: integer, height: integer, rgb: bytes } — 3 bytes per pixel, rows top to bottom
    IMAGE_MAX_SIDE: integer = 2048
    MAX_PIXELS: integer = 200000000
    DECODE_TIMEOUT: duration = 60 s
    SKIA_EXTENSIONS: set of text = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"] — every platform
    MAC_EXTENSIONS: set of text = [".heic", ".heif", ".tif", ".tiff", ".raw", ".avif"] — the system's ImageIO, on a Mac only
    decodeImage(path: text, maxSide: integer) -> later DecodedImage or nothing
      1. an extension in SKIA_EXTENSIONS: decoded with the picture library; otherwise on a Mac with ImageIO for
         MAC_EXTENSIONS; otherwise nothing (".svg" and the rest: name only)
      2. a picture whose header claims no pixels or more than MAX_PIXELS => nothing, before decoding
      3. a JPEG is subsampled by 1/8, 1/4 or 1/2 while decoding when that still leaves its longest side at maxSide or
         more (the largest such factor)
      4. pixels as stored, without colour-profile conversion; alpha dropped; a GIF gives its first frame; an
         incomplete file gives what was decoded
      5. turned upright by its EXIF orientation (all eight), then scaled down with bicubic resampling so the longest
         side is at most maxSide (never up), each side rounded and at least 1
      6. any failure, or more than DECODE_TIMEOUT => nothing, and the file keeps only its name vector
    fit(image: DecodedImage, maxSide: integer) -> DecodedImage — step 5's scaling
    the indexer asks for maxSide IMAGE_MAX_SIDE and embeds the result once, as the picture's one content vector
    examples
      fit({ width: 4000, height: 3000 }, 2048) => { width: 2048, height: 1536 }
      fit({ width: 320, height: 240 }, 2048) => { width: 320, height: 240 }
      decodeImage("rotated.jpg" stored 300 × 200 with EXIF orientation 6, 4096) => an upright 200 × 300 picture
      decodeImage("red-circle.png", 2048) => 320 × 240
      decodeImage("missing.png", 1024) => nothing;  decodeImage("bogus.png" holding text, 1024) => nothing
      decodeImage("photo.tiff", 2048) on Linux or Windows => nothing
  how
    decide RGB without alpha and without colour-profile conversion, EXIF orientation applied
      because that is what the reference loader does (Pillow's convert("RGB") and exif_transpose), and the model's
      vectors are checked against it (PR #9; MediaEmbeddingTests)
    decide the app's own picture library for the common formats
      because Avalonia already ships it, so it adds nothing to the app (SkiaImageDecoder)
    decide refuse pictures claiming more than 200 million pixels
      because a malicious header can claim anything (SkiaImageDecoder)
  uses      MediaDecoders
  sources
    src/Valafind.Core/Extraction/Media/SkiaImageDecoder.cs, MediaDecoder.cs (DecodeImageAsync, Guard), AvFoundationBackend.cs (DecodeImage)
    src/Valafind.Core/Indexing/IndexingCoordinator.cs, CoordinatorTuning (ImageMaxSide 2048), EmbedMedia
    tests/Valafind.Core.Tests/Extraction/MediaDecoderTests.cs; tests/Valafind.Core.Tests/Embedding/MediaEmbeddingTests.cs (rotated.jpg)
    PR #9, "Decoding: Pictures: SkiaSharp … Alpha is dropped and EXIF orientation applied. HEIC/TIFF go through ImageIO on macOS."
```
