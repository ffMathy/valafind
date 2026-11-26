# What the text model is given

The task prompts from the model card, how a document's title is fitted in, and how an input is cut to the token
budget.

```ail
node EmbeddingInputs : fn   level: system
  why   EmbeddingGemma was trained with these exact prompts; a question and a document only land near each other
        when each is framed the way the model expects.   <- EmbeddingModel
  what
    QUERY_PREFIX: text = "task: search result | query: "
    queryPrompt(query: text) -> text — QUERY_PREFIX + query
    documentPrompt(title: text or nothing, text: text) -> text — "title: " + normalizeTitle(title) + " | text: " + text
    normalizeTitle(title: text or nothing) -> text — "none" when blank; otherwise trimmed, with "\r\n", "\r", "\n" and
      "\t" each turned into one space
    TITLE_TOKEN_BUDGET: integer = 32
    RESERVED_PROMPT_TOKENS: integer = 44 — BOS and EOS (2), the document framing (6), the title budget (32) and 4 of slack
    buildDocumentPrompt(title: text or nothing, text: text) -> text — documentPrompt, with a normalised title longer
      than 32 characters cut to the characters of its first 32 tokens, trailing white space removed ("none" if
      nothing is left)
    buildInput(promptedText: text) -> list of integer — BOS, the text's tokens, EOS, at most maxTokens in all: the
      tokens are cut to maxTokens − 2, so BOS and EOS are always kept
      before tokenising, a text longer than (maxTokens − 2 + 64) × the longest token's length is cut to that many
      characters (never inside a surrogate pair), since no token can cover more
    prefixLengthWithinTokens(text: text, maxTokens: integer) -> integer — the length of the longest start of the
      text whose tokens are among its first maxTokens tokens: the whole length when it has at most that many, else the
      start offset of token number maxTokens
    the title given for each chunk is the file's name; for a file indexed by name only the text is its path below the
      indexed folder, its parts joined by " / "
    pictures, video and audio get no prompt at all (MediaInputs)
    examples
      queryPrompt("Which planet is known as the Red Planet?") => "task: search result | query: Which planet is known as the Red Planet?"
      documentPrompt(nothing, "body") => "title: none | text: body"
      documentPrompt("", "body") => "title: none | text: body"
      documentPrompt("   ", "body") => "title: none | text: body"
      documentPrompt("report.pdf", "body") => "title: report.pdf | text: body"
      documentPrompt("  padded.txt \n", "body") => "title: padded.txt | text: body"
      documentPrompt("two\nlines\tand\r\ntabs", "body") => "title: two lines and tabs | text: body"
      buildInput(a prompt of thousands of tokens) with maxTokens 80 => 80 ids: BOS, the prompt's first 78 tokens, EOS
      buildDocumentPrompt(t, c) => a prompt that starts with "title: Very_Long_File_Name" and has at most 510 tokens   where t = "Very_Long_File_Name_Component_" repeated 20 times + ".docx", c = a chunk of 468 tokens
      countTokens(the first prefixLengthWithinTokens(w, 468) characters of w) => between 466 and 469   where w = the 2000 words "word0 word1 … word1999"
  how
    decide BOS and EOS are added by the model, not by the tokenizer
      because truncation must keep both (ITextTokenizer)
    decide shorten long titles to 32 tokens instead of the text
      because a chunk measured to fit maxInputTokens must still fit when its file has a very long name
    decide a title of at most 32 characters is never measured
      because a token covers at least one character, so it fits
    rejected cutting the title at 32 characters because tokens, not characters, are the budget
  sources
    the EmbeddingGemma model card, "Prompt instructions"; PR #1
    src/Valafind.Core/Embedding/EmbeddingPrompts.cs; EmbeddingGemmaModel.cs, BuildDocumentPrompt, BuildInput,
      GetPrefixLengthWithinTokens; src/Valafind.Core/Indexing/IndexingCoordinator.cs, NameOnlyText
    tests/Valafind.Core.Tests/Embedding/EmbeddingUnitTests.cs; EmbeddingGemmaModelTests.cs,
      Long_inputs_are_truncated_keeping_bos_and_eos, Chunk_sized_text_fits_without_truncation_even_with_a_long_title
```
