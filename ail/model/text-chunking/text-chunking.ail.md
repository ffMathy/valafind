# Text into chunks

A document's text is cut into overlapping chunks that each fit the model's token budget, preferring to cut where the
text itself breaks: between paragraphs, then sentences, then lines, then words.

```ail
node TextChunking : fn   level: specialist
  why   The model reads at most 512 tokens at a time, and a meaning is sharpest for a passage about one thing; a cut in
        the middle of a sentence blurs both halves.   <- ModelAndIndex
  what
    type ChunkerOptions = { targetTokens: integer = 320, overlapTokens: integer = 48, maxChunks: integer = 12 }
    type TextChunk = { index: integer, start: integer, length: integer, text: text } — start and length index the
      original text; text is that span collapsed
    TextChunker(options: ChunkerOptions, tokenCounter: ((text) -> integer) or nothing) -> TextChunker — targetTokens and
      maxChunks at least 1 and overlapTokens at least 0, or refused
    TextChunker.chunk(text: text) -> list of TextChunk
    collapse(span: text) -> text — every run of white space becomes one character: a line break when the run held one
      (\n, \r, U+2028, U+2029, U+0085), else a space; nothing before the first or after the last character
    the app's chunker: the defaults, counting with the model's countTokens
    the rules of chunk:
      1. units: the text is read as sentences and lines; a sentence ends at . ! ? … (or 。！？．) followed by white space
         or the end, closing quotes and brackets included; each unit has the strength of the gap before it:
         paragraph (2 or more line breaks, or a form feed or U+2029) > sentence > line > word > none
      2. cost of a unit = tokens of its collapsed text minus the counter's tokens for "" (counted once per chunk);
         without a counter, (characters + 1) / 3.5; a unit costing more than the budget is split into words, and a
         word that is still too long into the longest pieces that fit, cut only between graphemes
      3. greedy: a chunk takes units while their cost fits targetTokens
      4. where to cut: the strongest boundary inside the chunk once it is at least half full; on a tie, the latest
      5. the collapsed chunk is measured again, and shortened unit by unit while it measures over targetTokens
      6. the next chunk starts with the last units of this one worth at most min(overlapTokens, targetTokens / 2)
      7. at most maxChunks chunks, the first ones; units stop being read once (maxChunks + 1) × targetTokens are read
    examples
      chunk("") => [];  chunk("   ") => [];  chunk("\r\n\t \n\n") => []
      chunk("\n\n  Hello   world.\n\nSecond\tparagraph here.  \n") => [{ index: 0, start: 4, length: 38, text: "Hello world.\nSecond paragraph here." }]
      two paragraphs of 35 words, targetTokens 50, overlapTokens 0, a counter of words => two chunks, each exactly one paragraph
      a 500-word sentence "w0 … w499", targetTokens 32, overlapTokens 4, words counted => every chunk ≤ 32 words, the last ending "w499"
      "e" + U+0301 repeated, targetTokens 25, no overlap => every chunk starts with "e" and ends with U+0301
      Chinese sentences without spaces, targetTokens 40, no counter => every chunk ≤ 40 characters and ends with 。 or ！
      a huge text, the defaults => exactly 12 chunks, with fewer than 20,000 calls of the counter
      targetTokens 0, maxChunks 0 or overlapTokens −1 => refused
  how
    decide 320 tokens, 48 of overlap, 12 chunks
      because 12 × (320 − 48) is about 3,300 new tokens, roughly the first 15,000 to 20,000 characters of prose, about
      6 s of CPU per file; it matches extraction's 32,000-character limit (ChunkerOptions)
    decide keep line breaks when collapsing
      because mail headers, lists, table rows and code keep their shape, and the search uses them as sentence
      boundaries for snippets
    decide measure the real chunk after the additive estimate
      because a sum of unit counts is slightly optimistic for BPE at the joins
    decide cut earlier than the greedy maximum only when the chunk is at least half full
      because a paragraph break near the start would otherwise make tiny chunks
    rejected embedding a whole long document because "unknown, confirm with the owner: text past the first 12 chunks of
      a long document cannot be found by meaning; is a deeper pass planned?"
  verify
    property: with a counter, every chunk measures at most targetTokens, unless one grapheme alone is more
    property: every chunk's text is collapse of the original span, which starts and ends with no white space and never
      inside a surrogate pair
    property: chunks advance: each starts after the previous one; with overlapTokens 0 they do not overlap
    property: every character that is not white space is in some chunk, when maxChunks does not cut the text short
  sources
    PR #1 ("Each document contributes its first ~32k characters (up to 12 chunks)"); README.md, "What gets indexed"
    src/Valafind.Core/Extraction/TextChunker.cs; src/Valafind.App/Services/CoreServicesFactory.cs
    tests/Valafind.Core.Tests/Extraction/TextChunkerTests.cs
```
