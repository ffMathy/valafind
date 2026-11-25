# Words of the question

How the question becomes words to look for in an excerpt and in a file name: words, a light stemmer, stopwords in
ten languages, compounds, and scripts written without spaces (TextAnalysis).

```ail
node TermMatching : fn   level: system
  why   The words that explain a match should light up whatever their form ("files", "filed", "filing") and whatever
        the language, without lighting up "the" or "and" in every sentence.   <- SearchRules
  what
    TextAnalysis.words(text: text) -> list of TextSpan — runs of letters, digits and combining marks
    TextAnalysis.stem(word: text) -> text — lower case; strips one of "ing" (words of 6 or more), "ed" (5 or more), "es" after ss, x, z, ch or sh (5 or more), or "s" not after s, u or i (4 or more); after "ing" or "ed" undoubles a final consonant other than l, s, z; then drops a final "e" from words of 4 or more
    TextAnalysis.isStopword(word: text) -> boolean — common function words of English, Danish, Norwegian, Swedish, German, Dutch, French, Spanish, Italian and Portuguese, case ignored
    TextAnalysis.queryTerms(query: text) -> { stems: set of text, lastTermPrefix: text or nothing } — the stems of the words of 3 or more letters (2 in a script without spaces) that are not stopwords; lastTermPrefix is the last word, lower case, when it is 3 or more letters, not a stopword, and the query ends inside it
    TextAnalysis.matches(text: text, span: TextSpan, stems: set of text, lastTermPrefix: text or nothing) -> boolean — a word of 2 or more letters whose stem is a term, or that starts with lastTermPrefix, or that is longer than 5 letters and starts with a term of 5 or more (a compound)
    TextAnalysis.lexicalMatches(text: text, stems: set of text, lastTermPrefix: text or nothing) -> list of TextSpan — the matching words; inside a word in Han, Hiragana, Katakana, Hangul or Thai, every longest stretch of 2 or more characters found in a term
    TextAnalysis.nameTokens(name: text) -> list of text — words split further where letters meet digits and at camel-case humps
    SearchService.nameTerms(query: text) -> set of text — like the stems of queryTerms
    SearchService.nameMatches(path: text, terms: set of text) -> boolean — a token of the name without its extension has a term as its stem; or a term of 5 or more letters, or in a script without spaces, occurs anywhere in that name, case ignored
    a set of text is written as a list in the examples, in any order
    MIN_TERM_LENGTH: integer = 3
    MIN_UNSPACED_TERM_LENGTH: integer = 2
    MIN_COMPOUND_PREFIX_LENGTH: integer = 5
    examples
      stem("files") => "fil";  stem("filing") => "fil";  stem("running") => "run";  stem("boxes") => "box"
      stem("matches") => "match";  stem("status") => "status";  stem("analysis") => "analysis";  stem("bus") => "bus"
      stem("Invoiced") => "invoic";  stem("budgets") => "budget"
      queryTerms("how much was the deposit for the apartment") => { stems: ["apartment", "deposit", "much"], lastTermPrefix: "apartment" }
      queryTerms("the lease where we") => { stems: ["leas"], lastTermPrefix: nothing }
      queryTerms("a") => { stems: [], lastTermPrefix: nothing };  queryTerms("契約") => { stems: ["契約"], lastTermPrefix: nothing }
      lexicalMatches("The invoices were filed late. Invoicing is slow.", q.stems, q.lastTermPrefix) => [{ start: 4, length: 8 }, { start: 18, length: 5 }, { start: 30, length: 9 }]   where q = queryTerms("invoice filing")
      lexicalMatches("Depositumbeløbet udbetales senere.", q.stems, q.lastTermPrefix) => [{ start: 0, length: 16 }]   where q = queryTerms("depositum")
      lexicalMatches("We agreed to cut the travel budget.", q.stems, q.lastTermPrefix) => [{ start: 21, length: 6 }, { start: 28, length: 6 }]   where q = queryTerms("travel budg")
      lexicalMatches("本賃貸借契約は、2024年に締結された。", q.stems, q.lastTermPrefix) => [{ start: 1, length: 2 }, { start: 4, length: 2 }]   where q = queryTerms("賃貸契約")
      lexicalMatches("Where is the deposit?", q.stems, q.lastTermPrefix) => []   where q = queryTerms("where is the")
      nameTokens("TaxReturn2024") => ["Tax", "Return", "2024"];  nameTokens("HTMLParser") => ["HTML", "Parser"]
      nameMatches("/d/TaxReturn2024.pdf", nameTerms("tax return")) => true
      nameMatches("/d/budgetplan.xlsx", nameTerms("budget")) => true
      nameMatches("/d/Mietvertragsentwurf.pdf", nameTerms("mietvertrag")) => true
      nameMatches("/d/invoice-march.pdf", nameTerms("invoices")) => true
      nameMatches("/d/Taxonomy.pdf", nameTerms("tax")) => false;  nameMatches("/d/report.pdf", nameTerms("port")) => false
      nameMatches("/d/notes.pdf", nameTerms("pdf")) => false
      isStopword("The") => true;  isStopword("budget") => false
  how
    decide a very small English stemmer, not a full one per language
      because "unknown, confirm with the owner"
    decide stopwords are left out of the terms
      because "the", "where", "with" … "would light up every sentence" (TextAnalysis)
    decide the word being typed matches as a prefix
      because the user is still typing it (TextAnalysis, queryTerms)
    decide long terms match the start of longer words
      because compounds then match their first part: "depositum" in "Depositumbeløbet", "lejekontrakt" in "Lejekontrakten" (TextAnalysis)
    decide in Chinese, Japanese, Korean and Thai, stretches of two characters count
      because "two characters are often a whole word" there, and words are not separated by spaces (TextAnalysis)
  sources
    src/Valafind.Core/Search/TextAnalysis.cs; src/Valafind.Core/Search/SearchService.cs, NameTerms, NameMatches
    tests/Valafind.Core.Tests/Search/TextAnalysisTests.cs, SearchServiceTests.cs
    examples run against the built app on 2026-11-25 (a small program calling TextAnalysis and SearchService by reflection)
```
