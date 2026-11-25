# The tokenizer

A re-implementation of the part of Hugging Face `tokenizers` that EmbeddingGemma's tokenizer.json uses, so that every
text becomes exactly the token ids the reference produces.

```ail
node Tokenizer : fn   level: specialist
  why   The model only means what it learned for the ids the reference tokenizer gives; one different id is a
        different vector. There is no .NET library that matches it on every text.   <- EmbeddingModel
  what
    type TextTokenizer = { bosId: integer, eosId: integer, padId: integer, maxTokenLength: integer,
      encode(text: text) -> list of { id: integer, offset: integer }, countTokens(text: text) -> integer }
    loadTokenizer(modelDirectory: text) -> TextTokenizer — tokenizer.json when it is there, else tokenizer.model
    for EmbeddingGemma 2: bosId 2 (<bos>), eosId 1 (<eos>), padId 0 (<pad>)
    encode runs, in order:
      1. added tokens with normalized false are split off the raw text, leftmost and longest first
      2. each piece between them is normalised by single-character replacements (Gemma: " " becomes "▁", U+2581);
         offsets stay those of the original text, since a replacement keeps one character as one
      3. added tokens with normalized true are split off the normalised piece
      4. the pre-tokenizer Split(pattern, MergedWithPrevious) ends a word after every occurrence of the pattern
      5. each word is BPE: every character is a vocabulary symbol, or else its UTF-8 bytes as <0xNN> tokens (byte
         fallback), or else <unk>, a run of unknown characters fusing into one <unk> (fuse_unk); then the adjacent pair
         with the lowest merge rank is merged, ties going to the leftmost pair, until no pair has a merge
      a lone surrogate is read as U+FFFD; offsets are the start of each token in the original text, the byte tokens of
        one character all sharing its offset
    countTokens(text) is the number of ids encode gives; maxTokenLength is the longest vocabulary or added token
    anything else in tokenizer.json is refused when loading: another model type than BPE, dropout, ignore_merges,
      subword prefixes or suffixes, added tokens with lstrip, rstrip or single_word, normalizers other than Sequence and
      single-character Replace, pre-tokenizers other than one Split on a plain string, post-processors other than
      TemplateProcessing
    toy: a tokenizer.json shaped like Gemma's, with vocabulary <pad> 0, <eos> 1, <bos> 2, <unk> 3, a 4, b 5, ab 6, ▁ 7,
      ▁a 8, <0xC3> 9, <0xA9> 10, bb 11, abb 12, "\n" 13, "\n\n" 14, <start> 15, <0xF0> 16, <0x9F> 17, <0x98> 18,
      <0x80> 19, < 20, s 21; merges in rank order (a, b), (b, b), (▁, a), (ab, b); added tokens 0 to 3, 13, 14 and 15;
      byte_fallback and fuse_unk on; gemma: EmbeddingGemma 2's own tokenizer.json
    examples
      toy.bosId => 2;  toy.eosId => 1;  toy.padId => 0;  toy.maxTokenLength => 7
      toy.ids("abb") => [12]
      toy.ids("bbab") => [11, 6]
      toy.ids("a a") => [4, 8];  toy.ids(" a") => [8];  toy.ids("  ") => [7, 7]
      toy.ids("é") => [9, 10];  toy.ids("😀") => [16, 17, 18, 19]
      toy.ids("xyz") => [3];  toy.ids("axya") => [4, 3, 4]
      toy.ids("a\n\n\na") => [4, 14, 13, 4];  toy.ids("<start>a") => [15, 4];  toy.ids("<bos>a<eos>") => [2, 4, 1];  toy.ids("<st") => [20, 21, 3]
      toy.encode("ab a\né😀") => ids [6, 8, 13, 9, 10, 16, 17, 18, 19] at offsets [0, 2, 4, 5, 5, 6, 6, 6, 6]
      toy.ids("a" + a lone high surrogate) => [4, 3];  toy.ids("") => []
      [2] + gemma.ids("task: search result | query: Which planet is known as the Red Planet?") + [1] => [2, 8071, 236787, 3927, 1354, 1109, 7609, 236787, 15311, 13401, 563, 3224, 618, 506, 4855, 38342, 236881, 1]
      [2] + gemma.ids("Hello   world\n\tTabs and  double  spaces — em-dash, emoji-free, 日本語テキスト, café") + [1] => [2, 9259, 139, 12392, 107, 255968, 76222, 532, 138, 7902, 138, 35220, 2192, 1092, 236772, 56057, 236764, 64334, 236772, 6796, 236764, 33375, 238582, 95830, 236764, 33443, 1]
  how
    decide merges by a priority queue keyed on (rank, position), with stale entries skipped
      because it mirrors tokenizers' Word::merge_all exactly, and Gemma's normaliser removes every space, so a whole
      chunk can be one word; this keeps it O(n log n)
    decide a character with no symbol, no byte tokens and no <unk> is dropped
      because the reference would raise an error; dropping keeps indexing going
    decide tokenizer.model through SentencePiece only when tokenizer.json is missing
      because that tokenizer differs from the reference on long runs of white space, added tokens written in the text
      and literal U+2581 characters (SentencePieceTextTokenizer)
  verify
    property: the offsets of encode(t) never decrease, start at 0 and stay below the length of t
    property: countTokens(t) is the number of ids encode(t) gives, for every t
  sources
    PR #1 ("token IDs match the reference tokenizer exactly")
    src/Valafind.Core/Embedding/HfBpeTokenizer.cs, SentencePieceTextTokenizer.cs, ITextTokenizer.cs
    tests/Valafind.Core.Tests/Embedding/HfBpeTokenizerTests.cs; EmbeddingGemmaModelTests.cs, Tokenizer_matches_reference_token_ids_exactly
    tests/Valafind.Core.Tests/Fixtures/embeddinggemma-reference.json, token_ids
```
