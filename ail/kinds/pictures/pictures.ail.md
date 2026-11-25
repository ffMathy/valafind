# Pictures

A picture is found by what it shows, including the words written in it, and by its name.

```ail
node Pictures : flow   level: experience
  why   People remember a photo by what was in it ("a red car in the snow") or a screenshot by the words on it, almost
        never by a camera's file name.   <- KindsOfFiles
  what
    PNG, JPEG, GIF (its first frame), BMP, WebP and icon files are read on every platform; HEIC, HEIF, TIFF, AVIF and
      camera raw files only on a Mac. Elsewhere those, and SVG drawings, are found by their name only.
    A picture is turned upright as the camera recorded it and made at most 2,048 pixels on its longest side before
      it is read; transparency is ignored.
    A picture larger than 200 million pixels, a damaged one, or one taking longer than a minute to decode is found by
      its name only.
    A result for a picture shows a small copy of it in place of the icon, and the details show it larger; a picture
      found by its name only shows the picture icon.
    Scenario: a picture is found by what it shows
      Given a picture of a red circle and a picture of a blue square, with names that mean nothing
      When the user searches for "a red circle"
      Then the red circle is listed above the blue square
      When the user searches for "a blue square"
      Then the blue square is listed above the red circle
    Scenario: a picture is found by the words written in it
      Given a picture of the word "BANANA" in large black letters and one of the word "TRACTOR"
      When the user searches for "a sign that says banana"
      Then the picture of "BANANA" is listed above the other
    Scenario: PNG, JPEG, GIF, BMP and WebP pictures are all read
      Given the same red circle saved as PNG, JPEG, GIF, BMP and WebP, and a blue square as PNG
      When the user searches for "a red circle"
      Then each of the five circles is listed above the square
    Scenario: a TIFF picture is found by its name only, except on a Mac
      Given a red circle saved as TIFF under the name "lighthouse-<code>.tiff", and the same circle as a PNG
      When the user searches for "lighthouse"
      Then the TIFF is listed
      When the user searches for "a red circle"
      Then the PNG is listed above the TIFF
  how
    decide a picture's content is read by the same model as text, into the same space
      because "a red car in the snow" must find the photo (PR #9, README)
      rejected reading the text in pictures with a separate OCR step because the model reads the words in a picture
      itself (README, "what they show, including text in them")
    decide decode with the picture library the app already ships, and the system's decoder on a Mac for its formats
      because it adds nothing to the app's size; HEIC and TIFF need the system's codecs (PR #9, "Decoding")
    decide HEIC and TIFF are not read on Windows and Linux
      because "unknown, confirm with the owner: Windows and Linux have codecs for these; should they be used?"
    decide pictures are scaled down while decoding
      because a 50-megapixel photo must never need 150 MB (SkiaImageDecoder)
  look
    a picture's result row shows its thumbnail in place of the kind icon; a TIFF on Linux shows the purple picture icon
  children  PictureDecoding
  sources
    README.md, "What gets indexed": Pictures (PNG, JPEG, GIF, BMP, WebP; HEIC/HEIF and TIFF on a Mac)
    PR #9 "Switch to EmbeddingGemma 2 …", Decoding; PR #22 "… show picture thumbnails in result rows"
    driven on Linux (Xvfb, openbox) 2026-11-25: red circles in PNG, JPEG, GIF, BMP, WebP and TIFF, a blue square, the
      words BANANA and TRACTOR, all drawn with ImageMagick; the TIFF was found by its name only, with the plain icon
    src/Valafind.Core/Extraction/Media/MediaDecoder.cs, SkiaImageDecoder.cs, AvFoundationBackend.cs (ImageExtensions)
```
