# Search rules

The search as plain code (SearchService): one question in, ranked results out, each with its excerpt and the spans
that explain it; and the optional second pass that finds the sentence closest in meaning.

```ail
node SearchRules : fn   level: system
  why   What a question finds, in what order, and why each result matched must be exact and testable without a
        desktop or the real model.   <- SearchFiles
  what
    type TextSpan = { start: integer, length: integer }
    type SearchResult = { path: text, fileName: text, directory: text, kind: FileKind, modifiedUtc: time, size: integer, score: number, snippet: text, highlights: list of TextSpan }
    SearchService.search(query: text, filter: { kinds: set of FileKind or nothing, modifiedAfter: time or nothing }, topK: integer) -> later list of SearchResult
      1. the query is trimmed; blank, or topK ≤ 0 => [], without embedding anything
      2. embed the query (the last query's vector is reused for the same text); ask the index for topK + min(topK, 32) hits, each a file with its best chunk
      3. add FILE_NAME_BOOST to the score of every hit whose file name matches the query (TermMatching.nameMatches), at most up to 1
      4. order by score, highest first, then by path (ordinal); keep topK
      5. each result's excerpt is cut around the chunk's sentence with the most query-term matches (the first on ties, the first sentence if none match); its highlights are every matching word in the excerpt
    SearchService.explain(query: text, results: list of SearchResult) -> later list of SearchResult
      for each of the first EXPLAIN_COUNT results with a score of at least MIN_EXPLAIN_RESULT_SCORE and an excerpt of two or more sentences: embed up to EXPLAIN_SENTENCES_PER_RESULT of its sentences in one batch, as documents titled "none"; the sentence with the highest similarity, if at least MIN_EXPLAIN_SENTENCE_SCORE, becomes the result's only highlight
      every other result is returned as it was; a model failure returns the results unchanged; cancellation is passed on
    FILE_NAME_BOOST: number = 0.03
    MAX_BOOST_CANDIDATES: integer = 32
    EXPLAIN_COUNT: integer = 2
    EXPLAIN_SENTENCES_PER_RESULT: integer = 4
    MIN_EXPLAIN_RESULT_SCORE: number = 0.70
    MIN_EXPLAIN_SENTENCE_SCORE: number = 0.65
    examples
      search("   ", none, 10) => []
      search("budgets") over notes.txt at 0.50 and Budget-2024.xlsx at 0.48 => [Budget-2024.xlsx at 0.51, notes.txt at 0.50]
      search("budget") over budget.txt at 0.99 => [budget.txt at 1]
      search("budget", none, 2) over a.txt 0.50, b.txt 0.49, budget.xlsx 0.48 => [budget.xlsx, a.txt]
      search("outage on the payment service and for") over incident-2024-11.pdf 0.50, "Brother HL-L2350 manual.pdf" 0.48, "Mother's day.jpg" 0.47, "Fortress and Theory.txt" 0.46 => the same order and scores
      search("marketing budget") over plan.txt "… The marketing budget for next year is set at two million. …" => highlights "marketing" and "budget"
      explain("marketing budget", that result at 0.70 or more) => one highlight, "The marketing budget for next year is set at two million."
      search("beach") over the picture "holiday beach.jpg" => snippet "", no highlights
  how
    decide the first pass is lexical only: one query embedding, one index search, no other model work
      because the list must appear while the user types (PR #1: "Lexical snippets come back fast")
    decide the boost is 0.03
      because "unknown, confirm with the owner"
    decide a name matches on a whole name token, or on a term of five letters or more, or in a script without spaces, anywhere in it
      because that "covers glued and compound names" without letting short terms match inside unrelated words, such as "tax" in "Taxonomy" or "port" in "report" (SearchService)
    decide up to 32 extra hits are fetched before boosting
      because "a file-name match ranked just below the cut can still be boosted into the results" (SearchService)
    decide the explanation runs in one batch with query priority
      because "the user is waiting on this batch", so it preempts an indexing batch (SearchService)
    decide for the top two results only, four sentences each, preferring sentences with query words
      because "unknown, confirm with the owner"
    decide no highlight for results under 0.70 or sentences under 0.65, and none when the excerpt has one sentence
      because EmbeddingGemma 2 scores unrelated text at 0.50 to 0.67 and related text from 0.72 (PR #9), and with one sentence "the lexical highlights already say more than one sentence would" (SearchService)
    decide ties keep the order of the path, compared character by character
      because "unknown, confirm with the owner"
  uses      TermMatching, SnippetRules, VectorSearch
  children  TermMatching, SnippetRules
  verify
    property: the scores never increase down the results
    property: explain never reorders, adds or removes results, and changes only highlights
  sources
    src/Valafind.Core/Search/SearchService.cs; tests/Valafind.Core.Tests/Search/SearchServiceTests.cs (the examples)
    PR #1 ("A second semantic pass then adds the why it matched highlight, and it is gated on relevance"); PR #9 (thresholds)
    the index search itself: ail/model/vector-search (VectorSearch)
```
