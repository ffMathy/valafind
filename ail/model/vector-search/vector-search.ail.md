# How a question is scored

A question becomes one vector; every chunk in the index is scored against it in memory with 8-bit integers, and the
files whose best chunk scored highest are scored again exactly from the stored float vectors.

```ail
node VectorSearch : fn   level: specialist
  why   A home folder can hold a million chunks, and the launcher must answer while the user types; an exact scan of
        float vectors is too slow and too big, an approximate one alone ranks close files wrongly.   <- ModelAndIndex
  what
    the query: q = truncateAndNormalize(sentence_embedding of [BOS] + ids(queryPrompt(trim(text))) + [EOS], dimensions),
      at most 512 tokens; an empty trimmed text finds nothing; the vector of the last text asked is kept and reused
      while the same text is asked again
    the score of a chunk with stored vector v is the cosine s = q · v / (|q| |v|), between −1 and 1; a file's score
      is its best chunk's
    quantize(v: list of number) -> { codes: list of integer, scale: number } — scale = max |vᵢ| / 127,
      codesᵢ = vᵢ / scale rounded half to even and clamped to −127…127; a zero or non-finite vector => codes 0, scale 0
    approximate(row, q) = dotInt8(row codes, q codes) × row scale × q scale — the integer dot product is exact
    IndexStore.search(query: list of number, filter: { kinds: set of integer or nothing, modifiedAfter: time or nothing },
      topK: integer) -> list of { path: text, kind: integer, size: integer, modifiedUtc: time, score: number,
      chunkIndex: integer, chunkText: text }
      1. topK ≤ 0, or a query that is zero or not finite after normalising => []; a query of another length => refused
      2. scan every live row of the matrix that passes the filter: kind in kinds (an empty set means every kind) and
         modification time strictly after modifiedAfter; keep each file's best approximate score
      3. candidates: the max(4 × topK, 16) files with the highest of those
      4. read every chunk of the candidates from the database, check the filter again, normalise its float vector,
         s = q · v; keep each file's best chunk
      5. order by s, highest first, then by path (ordinal); keep topK
    the search on top asks for topK + min(topK, 32) hits, adds 0.03 (at most up to 1) to a hit whose file name matches
      the query's words, orders again by score and then path, and keeps topK
    the matrix: every chunk's int8 codes and scale, with its file id, kind and modification time beside it, in
      segments of 16,384 rows, about rows × (dimensions + 21) bytes; a file's rows are contiguous; a replaced or removed
      file leaves dead rows, and once they are more than a quarter of all rows the matrix is rebuilt without them and
      swapped in while searches go on; from 131,072 rows the segments are scanned in parallel
    examples
      quantize([0.5, −1, 0.25, 0]) => { codes: [64, −127, 32, 0], scale: 1 / 127 }
      quantize([0, 0, 0, 0]) => { codes: [0, 0, 0, 0], scale: 0 };  quantize([NaN, 1, 2, 3]).scale => 0
      search(axis 0, none, 10) over notes.txt with one chunk on axis 0 => [{ path: notes.txt, chunkIndex: 0, chunkText: "hello world", score: 1 }]
      search(axis 0, none, 10) over many (chunks on axis 1, on (0.9, 0.1), on (0.5, 0.5)) and one (on (0.7, 0.3)) => [many with chunkIndex 1 and score 0.9939, one]
      search(42 × axis 3) over a chunk on axis 3 => one hit of score 1
      search(the zero vector) => [];  search(all NaN) => [];  search(axis 3, none, 0) => []
      search(q, kinds { PDF }) over old.pdf, new.pdf and new.cs => old.pdf and new.pdf
      search(q, modifiedAfter t) over old.pdf (modified t), new.pdf and new.cs (t + 10 days) => new.cs and new.pdf
      search(q, kinds { image }) over those => []
      search(a random vector, none, 5000) over 700 files => 700 hits, all different files
      search(axis 0) after a.txt (chunks on axes 0 and 1) is upserted again with one chunk "new" on axis 2 => [{ path: a.txt, chunkText: "new", score: 0 }]
    probes
      two chunks of one file with exactly the same exact score: which chunk's text is shown
  how
    decide int8 codes in memory, exact float scores for the candidates
      because the matrix is 4 times smaller than float32 and fast with SIMD, and re-scoring makes the final order exact
      (PR #1, "SQLite plus an int8 vector matrix searched with SIMD, then exact re-scoring")
    decide brute force over every row, not an approximate nearest-neighbour index
      because "unknown, confirm with the owner"
    decide four times topK candidates, at least 16
      because quantisation can swap files whose scores are close; the candidates cover them (recall at 10 ≥ 0.98)
    decide -128 is never a code
      because then every product fits a signed 16-bit lane (VectorMath.DotInt8)
  verify
    property: recall at 10 of search against an exact brute force over 5,000 random 256-value vectors is at least 0.98
    property: the scores returned never increase down the list
    property: after any sequence of upserts and removals, compaction leaves dead rows at most a quarter of all rows and
      the same top results
  sources
    PR #1; PR #21 (memory ≈ chunks × 789 bytes at 768)
    src/Valafind.Core/Storage/SqliteIndexStore.cs, Search and Rescore; QuantizedVectorIndex.cs; VectorMath.cs
    src/Valafind.Core/Search/SearchService.cs, SearchAsync and EmbedQueryAsync (FileNameBoost 0.03, MaxBoostCandidates 32)
    tests/Valafind.Core.Tests/Storage/SqliteIndexStoreTests.cs, VectorIndexTests.cs
```
