using Valafind.Core.Abstractions;
using Valafind.Core.Search;
using Xunit;

namespace Valafind.Core.Tests.Search;

public sealed class TextAnalysisTests
{
    [Theory]
    [InlineData("file", "files", "filed", "filing")]
    [InlineData("invoice", "invoices", "invoicing", "Invoiced")]
    [InlineData("run", "running", "runs", "RUN")]
    [InlineData("box", "boxes", "Box", "box")]
    [InlineData("match", "matches", "matched", "matching")]
    public void Stem_conflates_common_inflections(params string[] words)
    {
        var stems = words.Select(TextAnalysis.Stem).Distinct().ToList();
        Assert.Single(stems);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("analysis")]
    [InlineData("the")]
    [InlineData("bus")]
    public void Stem_leaves_non_plurals_alone(string word) => Assert.Equal(word, TextAnalysis.Stem(word));

    [Fact]
    public void Words_are_unicode_aware()
    {
        const string text = "Grüße, naïve café — 日本語 x2 😀ok";
        var words = TextAnalysis.Words(text).Select(s => text.Substring(s.Start, s.Length)).ToList();
        Assert.Equal(["Grüße", "naïve", "café", "日本語", "x2", "ok"], words);
    }

    [Fact]
    public void Query_terms_ignore_short_words_and_expose_typed_prefix()
    {
        var (stems, last) = TextAnalysis.QueryTerms("an ox ate invoic");
        Assert.Equal(["ate", "invoic"], stems.Order(StringComparer.Ordinal));
        Assert.Equal("invoic", last);
        Assert.Null(TextAnalysis.QueryTerms("invoice ").LastTermPrefix);

        const string text = "The invoices arrived.";
        var match = Assert.Single(TextAnalysis.LexicalMatches(text, stems, last));
        Assert.Equal("invoices", text.Substring(match.Start, match.Length));
    }

    private static List<string> Highlighted(string text, string query)
    {
        var (stems, last) = TextAnalysis.QueryTerms(query);
        return TextAnalysis.LexicalMatches(text, stems, last).Select(s => text.Substring(s.Start, s.Length)).ToList();
    }

    [Theory]
    [InlineData("本賃貸借契約は、2024年に締結された。", "賃貸契約", new[] { "賃貸", "契約" })]
    [InlineData("本契約の期間は一年とする。", "契約", new[] { "契約" })]
    [InlineData("租赁合同已于去年签署。", "租赁合同", new[] { "租赁合同" })]
    [InlineData("임대차계약서를 확인했습니다", "계약서", new[] { "계약서" })]
    [InlineData("สัญญาเช่าบ้านหลังนี้", "สัญญาเช่า", new[] { "สัญญาเช่า" })]
    public void Unspaced_scripts_highlight_query_substrings_inside_runs(string text, string query, string[] expected) =>
        Assert.Equal(expected, Highlighted(text, query));

    [Fact]
    public void Unspaced_script_matching_needs_two_chars_and_ignores_unrelated_runs()
    {
        Assert.Empty(Highlighted("本賃貸借契約は", "約束")); // single shared char "約" is not enough
        Assert.Empty(Highlighted("今日は晴れです", "契約"));
        Assert.Equal(["契約"], Highlighted("Lease 契約 signed", "契約 lease").Where(w => w != "Lease"));
    }

    [Theory]
    [InlineData("Depositumbeløbet udbetales senere.", "depositum", "Depositumbeløbet")]
    [InlineData("Lejekontrakten er underskrevet.", "lejekontrakt udløb", "Lejekontrakten")]
    [InlineData("Die Mietvertragsdauer beträgt ein Jahr.", "mietvertrag", "Mietvertragsdauer")]
    public void Long_terms_match_compounds_by_prefix(string text, string query, string expected) =>
        Assert.Contains(expected, Highlighted(text, query));

    [Fact]
    public void Short_terms_do_not_match_compound_prefixes()
    {
        // "tax" (3 chars) must not light up "taxonomy"; only whole stems match for short terms.
        Assert.Empty(Highlighted("The taxonomy is new.", "tax ok"));
        Assert.Equal(["tax"], Highlighted("The tax is new.", "tax ok"));
    }

    [Theory]
    [InlineData("TaxReturn2024", new[] { "Tax", "Return", "2024" })]
    [InlineData("HTMLParser", new[] { "HTML", "Parser" })]
    [InlineData("incident-2024-11", new[] { "incident", "2024", "11" })]
    [InlineData("Brother HL-L2350 manual", new[] { "Brother", "HL", "L", "2350", "manual" })]
    [InlineData("賃貸契約書", new[] { "賃貸契約書" })]
    public void Name_tokens_split_on_separators_digits_and_camel_case(string name, string[] expected) =>
        Assert.Equal(expected, TextAnalysis.NameTokens(name));

    [Theory]
    [InlineData("the", true)]
    [InlineData("The", true)]
    [InlineData("und", true)]
    [InlineData("til", true)]
    [InlineData("pour", true)]
    [InlineData("budget", false)]
    [InlineData("payment", false)]
    public void Stopwords_cover_common_function_words(string word, bool expected) =>
        Assert.Equal(expected, TextAnalysis.IsStopword(word));

    [Fact]
    public void Sentences_split_on_punctuation_and_lines()
    {
        const string text = "Hello there. How are you?! Fine…\nNext line\r\n\r\n  Last one (really).\"  ";
        var sentences = TextAnalysis.Sentences(text).Select(s => text.Substring(s.Start, s.Length)).ToList();
        Assert.Equal(["Hello there.", "How are you?!", "Fine…", "Next line", "Last one (really).\""], sentences);
    }

    [Fact]
    public void Long_sentences_are_cut_at_word_boundaries()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 200));
        var sentences = TextAnalysis.Sentences(text);
        Assert.True(sentences.Count > 1);
        Assert.All(sentences, s =>
        {
            Assert.InRange(s.Length, 1, TextAnalysis.MaxSentenceChars);
            Assert.Equal("word", text.Substring(s.Start, 4));
            Assert.Equal("word", text.Substring(s.Start + s.Length - 4, 4));
        });
    }

    [Fact]
    public void Candidate_sentences_cap_prefers_matching_ones_in_text_order()
    {
        var parts = Enumerable.Range(0, 20).Select(i => i is 3 or 17 ? $"Sentence {i} mentions budget." : $"Sentence {i} is filler.");
        var text = string.Join(' ', parts);
        var (stems, last) = TextAnalysis.QueryTerms("budget");
        var chosen = TextAnalysis.CandidateSentences(text, 12, stems, last);
        Assert.Equal(12, chosen.Count);
        var chosenText = chosen.Select(s => text.Substring(s.Start, s.Length)).ToList();
        Assert.Contains("Sentence 17 mentions budget.", chosenText);
        Assert.Contains("Sentence 3 mentions budget.", chosenText);
        Assert.Equal(chosen.OrderBy(s => s.Start), chosen);
    }

    [Fact]
    public void Query_terms_skip_stopwords_including_the_word_being_typed()
    {
        var (stems, last) = TextAnalysis.QueryTerms("the lease where we negotiated the");
        Assert.DoesNotContain("the", stems);
        Assert.DoesNotContain("where", stems);
        Assert.Contains(TextAnalysis.Stem("lease"), stems);
        Assert.Null(last); // "the" is still being typed, but highlighting every "the…" word helps nobody.

        Assert.Equal("negoti", TextAnalysis.QueryTerms("lease negoti").LastTermPrefix);
    }

    [Fact]
    public void Text_without_sentence_punctuation_is_split_into_word_windows()
    {
        const string code = "def retry(fn, attempts=5): for i in range(attempts): try: return fn() except IOError: time.sleep(2 ** i)";
        var windows = TextAnalysis.CandidateSentences(code, 4, [], null);
        Assert.True(windows.Count >= 2, "code must yield several candidate spans");
        Assert.All(windows, w => Assert.InRange(w.Start + w.Length, 1, code.Length));
        for (var k = 1; k < windows.Count; k++)
            Assert.True(windows[k].Start >= windows[k - 1].Start + windows[k - 1].Length, "windows must not overlap");
        Assert.Equal("def", code.Substring(windows[0].Start, 3));
    }
}

public sealed class SnippetTests
{
    [Fact]
    public void Short_text_is_returned_whole_with_collapsed_whitespace()
    {
        const string text = "  Line one.\n\n\tLine   two.  ";
        var snippet = Snippet.Build(text, new TextSpan(14, 12));
        // The run between the lines held line breaks: it collapses to one '\n' (a sentence boundary), the rest to spaces.
        Assert.Equal("Line one.\nLine two.", snippet.Text);
        var mapped = snippet.MapSourceSpan(new TextSpan(14, 12));
        Assert.NotNull(mapped);
        Assert.Equal("Line two.", snippet.Text.Substring(mapped.Value.Start, mapped.Value.Length));
    }

    [Fact]
    public void Long_text_window_is_centred_on_focus_with_ellipses_at_word_boundaries()
    {
        var words = Enumerable.Range(0, 300).Select(i => $"w{i:D3}").ToArray();
        var text = string.Join(' ', words);
        var focusStart = text.IndexOf("w150", StringComparison.Ordinal);
        var snippet = Snippet.Build(text, new TextSpan(focusStart, 4));

        Assert.StartsWith("…", snippet.Text, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet.Text, StringComparison.Ordinal);
        Assert.InRange(snippet.Text.Length, 200, Snippet.TargetLength + 2);
        var inner = snippet.Text[1..^1];
        Assert.All(inner.Split(' '), w => Assert.Matches("^w\\d{3}$", w)); // no cut words
        var mapped = snippet.MapSourceSpan(new TextSpan(focusStart, 4))!.Value;
        Assert.Equal("w150", snippet.Text.Substring(mapped.Start, mapped.Length));
        Assert.InRange(mapped.Start, 90, 150); // roughly centred
    }

    [Fact]
    public void Focus_near_end_keeps_full_window_and_no_trailing_ellipsis()
    {
        var text = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"w{i:D3}"));
        var snippet = Snippet.Build(text, new TextSpan(text.Length - 4, 4));
        Assert.StartsWith("…", snippet.Text, StringComparison.Ordinal);
        Assert.EndsWith("w299", snippet.Text, StringComparison.Ordinal);
        Assert.InRange(snippet.Text.Length, 200, Snippet.TargetLength + 1);
    }

    [Fact]
    public void Spans_outside_window_map_to_null_and_partial_spans_are_clipped()
    {
        var text = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"w{i:D3}"));
        var snippet = Snippet.Build(text, new TextSpan(0, 4));
        Assert.Null(snippet.MapSourceSpan(new TextSpan(text.Length - 10, 10)));
        var clipped = snippet.MapSourceSpan(new TextSpan(0, text.Length))!.Value;
        Assert.Equal(0, clipped.Start);
        Assert.Equal(snippet.Text.Length - 1, clipped.Length); // everything but the trailing "…"
    }

    [Fact]
    public void Whitespace_only_text_gives_empty_snippet() =>
        Assert.Equal("", Snippet.Build(" \n\t ", new TextSpan(0, 1)).Text);
}
