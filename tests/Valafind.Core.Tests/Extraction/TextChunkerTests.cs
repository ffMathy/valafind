using System.Text;
using Valafind.Core.Abstractions;
using Valafind.Core.Extraction;
using Xunit;

namespace Valafind.Core.Tests.Extraction;

public sealed class TextChunkerTests
{
    private static readonly string[] Vocabulary =
    [
        "glacier", "river", "mountain", "the", "a", "valley", "ice", "melt", "summer", "winter", "data", "report",
        "analysis", "of", "and", "with", "flow", "rate", "measured", "station", "north", "south", "season",
    ];

    // A deterministic word counter plus two "special" tokens per call, like BOS/EOS in a real tokenizer.
    private static int WordCounter(string s) =>
        2 + s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Prose(int paragraphs, int sentencesPerParagraph, int seed = 7)
    {
        var random = new Random(seed);
        var sb = new StringBuilder();
        for (var p = 0; p < paragraphs; p++)
        {
            if (p > 0)
            {
                sb.Append(p % 3 == 0 ? "\r\n\r\n" : "\n\n  ");
            }

            for (var s = 0; s < sentencesPerParagraph; s++)
            {
                var words = random.Next(5, 18);
                for (var w = 0; w < words; w++)
                {
                    var word = Vocabulary[random.Next(Vocabulary.Length)];
                    sb.Append(w == 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word);
                    sb.Append(w == words - 1 ? (s % 4 == 3 ? "!" : ".") : (w % 7 == 6 ? "\t " : " "));
                }

                if (s < sentencesPerParagraph - 1)
                {
                    sb.Append(s % 5 == 4 ? "\n" : " ");
                }
            }
        }

        return sb.ToString();
    }

    private static void AssertOffsetsMapBack(string original, IReadOnlyList<TextChunk> chunks)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            Assert.Equal(i, chunk.Index);
            Assert.InRange(chunk.Start, 0, original.Length - 1);
            Assert.InRange(chunk.Length, 1, original.Length - chunk.Start);
            var span = original.Substring(chunk.Start, chunk.Length);
            Assert.False(char.IsWhiteSpace(span[0]));
            Assert.False(char.IsWhiteSpace(span[^1]));
            Assert.Equal(TextChunker.Collapse(span), chunk.Text);
            Assert.DoesNotContain("  ", chunk.Text, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', chunk.Text);
            Assert.DoesNotContain('\t', chunk.Text);
            Assert.False(char.IsLowSurrogate(original[chunk.Start]), "chunk starts inside a surrogate pair");
            Assert.False(char.IsHighSurrogate(original[chunk.Start + chunk.Length - 1]), "chunk ends inside a surrogate pair");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t \n\n")]
    public void EmptyOrWhitespace_ProducesNoChunks(string text)
    {
        Assert.Empty(new TextChunker(new ChunkerOptions()).Chunk(text));
    }

    [Fact]
    public void ShortText_IsOneCollapsedChunkWithOriginalOffsets()
    {
        const string text = "\n\n  Hello   world.\n\nSecond\tparagraph here.  \n";
        var chunks = new TextChunker(new ChunkerOptions()).Chunk(text);

        var chunk = Assert.Single(chunks);
        Assert.Equal(text.IndexOf('H', StringComparison.Ordinal), chunk.Start);
        Assert.Equal("Hello world. Second paragraph here.", chunk.Text);
        Assert.Equal("Hello   world.\n\nSecond\tparagraph here.", text.Substring(chunk.Start, chunk.Length));
    }

    [Fact]
    public void TokenCounter_EveryChunkFitsTarget_AndOffsetsMapBack()
    {
        var text = Prose(paragraphs: 30, sentencesPerParagraph: 6);
        var options = new ChunkerOptions { TargetTokens = 64, OverlapTokens = 12, MaxChunks = 1000 };
        var chunks = new TextChunker(options, WordCounter).Chunk(text);

        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.True(WordCounter(c.Text) <= 64, $"chunk {c.Index} has {WordCounter(c.Text)} tokens"));
        AssertOffsetsMapBack(text, chunks);
    }

    [Fact]
    public void DefaultEstimator_EveryChunkFitsConservativeEstimate()
    {
        var text = Prose(paragraphs: 20, sentencesPerParagraph: 5, seed: 3);
        var options = new ChunkerOptions { TargetTokens = 50, OverlapTokens = 10, MaxChunks = 1000 };
        var chunks = new TextChunker(options).Chunk(text);

        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.True(Math.Ceiling(c.Text.Length / 3.5) <= 50));
        AssertOffsetsMapBack(text, chunks);
    }

    [Fact]
    public void Chunks_CoverAllNonWhitespaceText_AndOverlap()
    {
        var text = Prose(paragraphs: 12, sentencesPerParagraph: 8, seed: 11);
        var options = new ChunkerOptions { TargetTokens = 80, OverlapTokens = 20, MaxChunks = 1000 };
        var chunks = new TextChunker(options, WordCounter).Chunk(text);

        var covered = new bool[text.Length];
        foreach (var c in chunks)
        {
            Array.Fill(covered, true, c.Start, c.Length);
        }

        for (var i = 0; i < text.Length; i++)
        {
            Assert.True(covered[i] || char.IsWhiteSpace(text[i]), $"char {i} ('{text[i]}') not covered");
        }

        var overlapping = 0;
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].Start > chunks[i - 1].Start, "chunks must advance");
            var prevEnd = chunks[i - 1].Start + chunks[i - 1].Length;
            if (chunks[i].Start < prevEnd)
            {
                overlapping++;
                // Overlap stays roughly within the configured budget.
                var shared = TextChunker.Collapse(text.AsSpan(chunks[i].Start, prevEnd - chunks[i].Start));
                Assert.True(WordCounter(shared) - 2 <= 20, $"overlap too large: {shared}");
            }
        }

        Assert.True(overlapping >= chunks.Count / 2, $"expected most chunks to overlap, got {overlapping}/{chunks.Count}");
    }

    [Fact]
    public void ZeroOverlap_ProducesDisjointChunks()
    {
        var text = Prose(paragraphs: 10, sentencesPerParagraph: 6);
        var options = new ChunkerOptions { TargetTokens = 60, OverlapTokens = 0, MaxChunks = 1000 };
        var chunks = new TextChunker(options, WordCounter).Chunk(text);

        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].Start >= chunks[i - 1].Start + chunks[i - 1].Length);
        }
    }

    [Fact]
    public void MaxChunks_KeepsTheFirstChunks()
    {
        var text = Prose(paragraphs: 200, sentencesPerParagraph: 6);
        var options = new ChunkerOptions { TargetTokens = 40, OverlapTokens = 8, MaxChunks = 5 };
        var chunks = new TextChunker(options, WordCounter).Chunk(text);

        Assert.Equal(5, chunks.Count);
        Assert.Equal(0, chunks[0].Start);
        Assert.True(chunks[^1].Start + chunks[^1].Length < text.Length / 10);
    }

    [Fact]
    public void PrefersParagraphBoundaries()
    {
        // Two paragraphs of ~35 words each; target 50 fits one paragraph but not both.
        var p1 = string.Join(' ', Enumerable.Range(0, 7).Select(i => $"First paragraph sentence number {i}.")); // 35 words
        var p2 = string.Join(' ', Enumerable.Range(0, 7).Select(i => $"Second paragraph sentence number {i}."));
        var text = p1 + "\n\n" + p2;
        var options = new ChunkerOptions { TargetTokens = 50, OverlapTokens = 0, MaxChunks = 10 };
        var chunks = new TextChunker(options, WordCounter).Chunk(text);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(p1, chunks[0].Text);
        Assert.Equal(p2, chunks[1].Text);
    }

    [Fact]
    public void LongSentence_IsSplitAtWordBoundaries()
    {
        var text = string.Join(' ', Enumerable.Range(0, 500).Select(i => $"w{i}"));
        var options = new ChunkerOptions { TargetTokens = 32, OverlapTokens = 4, MaxChunks = 1000 };
        var chunks = new TextChunker(options, WordCounter).Chunk(text);

        Assert.True(chunks.Count >= 500 / 30);
        Assert.All(chunks, c => Assert.True(WordCounter(c.Text) <= 32));
        AssertOffsetsMapBack(text, chunks);
        Assert.EndsWith("w499", chunks[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlongWord_IsHardSplitWithinBudget()
    {
        var word = string.Concat(Enumerable.Repeat("abcdefghij", 300)); // 3000 chars, no spaces
        var text = "Intro sentence. " + word + " outro.";
        static int CharCounter(string s) => s.Length; // 1 token per char
        var options = new ChunkerOptions { TargetTokens = 100, OverlapTokens = 10, MaxChunks = 1000 };
        var chunks = new TextChunker(options, CharCounter).Chunk(text);

        Assert.All(chunks, c => Assert.True(c.Text.Length <= 100, $"chunk of {c.Text.Length} chars"));
        AssertOffsetsMapBack(text, chunks);
        Assert.True(chunks.Count >= 30);
        var wordStart = text.IndexOf(word, StringComparison.Ordinal);
        for (var i = wordStart; i < wordStart + word.Length; i++)
        {
            Assert.Contains(chunks, c => c.Start <= i && i < c.Start + c.Length);
        }
    }

    [Fact]
    public void NeverSplitsSurrogatePairs()
    {
        // 2000 emoji (each a surrogate pair) and some astral CJK, without spaces.
        var sb = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            sb.Append(i % 3 == 0 ? "\U0001F600" : i % 3 == 1 ? "\U00020BB7" : "x");
        }

        var text = sb.ToString();
        foreach (var counter in new Func<string, int>?[] { null, s => s.Length, s => Encoding.UTF8.GetByteCount(s) })
        {
            var options = new ChunkerOptions { TargetTokens = 37, OverlapTokens = 5, MaxChunks = 10_000 };
            var chunks = new TextChunker(options, counter).Chunk(text);
            Assert.True(chunks.Count > 10);
            AssertOffsetsMapBack(text, chunks);
            Assert.All(chunks, c => Assert.Equal(c.Text, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(c.Text))));
        }
    }

    [Fact]
    public void DoesNotSplitCombiningSequences()
    {
        var text = string.Concat(Enumerable.Repeat("e\u0301", 400)); // é as e + combining acute
        var options = new ChunkerOptions { TargetTokens = 25, OverlapTokens = 0, MaxChunks = 1000 };
        var chunks = new TextChunker(options, s => s.Length).Chunk(text);

        Assert.All(chunks, c => Assert.Equal('e', c.Text[0]));
        Assert.All(chunks, c => Assert.Equal('\u0301', c.Text[^1]));
    }

    [Fact]
    public void HandlesCjkSentencesWithoutSpaces()
    {
        var text = string.Concat(Enumerable.Repeat("氷河は夏に溶ける。川の流量が増える！", 100));
        var options = new ChunkerOptions { TargetTokens = 40, OverlapTokens = 8, MaxChunks = 1000 };
        var chunks = new TextChunker(options, s => s.Length).Chunk(text);

        Assert.All(chunks, c => Assert.True(c.Text.Length <= 40));
        Assert.All(chunks, c => Assert.True(c.Text[^1] is '\u3002' or '\uFF01', $"chunk should end at a sentence: {c.Text}"));
        AssertOffsetsMapBack(text, chunks);
    }

    [Fact]
    public void HugeInput_StopsEarlyAndQuickly()
    {
        var text = Prose(paragraphs: 5000, sentencesPerParagraph: 5); // several MB
        var calls = 0;
        var options = new ChunkerOptions(); // 320 / 48 / 48
        var chunks = new TextChunker(options, s => { calls++; return WordCounter(s); }).Chunk(text);

        Assert.Equal(48, chunks.Count);
        Assert.True(calls < 20_000, $"token counter called {calls} times");
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(new ChunkerOptions { TargetTokens = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(new ChunkerOptions { MaxChunks = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(new ChunkerOptions { OverlapTokens = -1 }));
    }
}
