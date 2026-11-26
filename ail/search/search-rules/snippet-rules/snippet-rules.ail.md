# Sentences and the excerpt

How a chunk of a file is split into sentences, which sentence the excerpt is centred on, and how the excerpt is cut
(TextAnalysis.sentences, Snippet).

```ail
node SnippetRules : fn   level: system
  why   The excerpt must show the part of the file that answers the question, cut at words, and keep sentences apart
        so that one sentence can be highlighted.   <- SearchRules
  what
    TextAnalysis.sentences(text: text) -> list of TextSpan — split after ".", "!", "?", "…" (with following ".!?…" and closing quotes or brackets) when white space or the end follows; after "。", "！", "？" always; and at every line break; each trimmed, kept only with a letter or digit in it; a sentence over 240 characters is cut at a space in its second half, else at 240
    TextAnalysis.candidateSentences(text: text, max: integer, stems: set of text, lastTermPrefix: text or nothing) -> list of TextSpan — the sentences, or, with fewer than two, windows of 9 words; when more than max, those with the most query matches, then the earliest, back in text order
    Snippet.build(source: text, focus: TextSpan, targetLength: integer = 240) -> { text: text, sourceStart: integer, sourceEnd: integer }
      1. a window of targetLength centred on the focus (starting at the focus when it is longer), moved to fit inside the source
      2. each edge moves inwards by up to 24 characters to a space, so no word is cut
      3. every run of white space becomes one "\n" when it held a line break, else one " "; leading white space is dropped
      4. "…" is put before it when it starts after the source's start, and after it when it ends before the source's end
    SNIPPET_LENGTH: integer = 240
    MAX_SENTENCE_CHARS: integer = 240
    WINDOW_WORDS: integer = 9
    the excerpt of a result: build(chunk, the sentence with the most query matches, the first of them on a tie, the first sentence when nothing matches)
    the launcher shows "\n" in an excerpt as a space
    examples
      sentences("Hi there. How are you?\nSubject: x") => [{ start: 0, length: 9 }, { start: 10, length: 12 }, { start: 23, length: 10 }]
      sentences("Ends with a question?! Next one") => [{ start: 0, length: 22 }, { start: 23, length: 8 }]
      sentences("one\n\ntwo") => [{ start: 0, length: 3 }, { start: 5, length: 3 }]
      build(farm, { start: 704, length: 58 }).text => "…Sentence number 8 talks about the weather in the north of the country and nothing else. The pineapple harvest failed in June because of the frost. Line 1 lists tractor parts for the workshop. Line 2 lists tractor parts for the workshop.…"
      build("Short text.\n\n  Two   lines.", { start: 0, length: 3 }).text => "Short text.\nTwo lines."
    in these examples, farm (942 characters, the pineapple sentence at 704) is eight sentences "Sentence number N talks about the weather in the north of the country and nothing else." (N from 1 to 8), then "The pineapple harvest failed in June because of the frost.", then four "Line N lists tractor parts for the workshop.", all separated by single spaces
  how
    decide a line break ends a sentence
      because mail headers, list items, table rows and code otherwise fused into one sentence, and "Why it matched" highlighted whole header blocks (PR #3)
    decide the excerpt is about 240 characters
      because "unknown, confirm with the owner"
    decide text without sentence punctuation is cut into windows of nine words for the explanation
      because code, tables and lists have "no sentence punctuation to split on" (TextAnalysis)
    decide the "…" at a cut end is never part of a sentence the explanation embeds
      because "unknown, confirm with the owner"
  sources
    src/Valafind.Core/Search/Snippet.cs; src/Valafind.Core/Search/TextAnalysis.cs, Sentences, CandidateSentences, WordWindows
    PR #3 ("Bug fix found while taking the screenshots": line breaks as sentence boundaries)
    examples run against the built app on 2026-11-25 (a small program calling Snippet.Build and TextAnalysis.Sentences
      by reflection); farm matches the file driven in SearchFiles
```
