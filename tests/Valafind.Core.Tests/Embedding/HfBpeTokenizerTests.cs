#pragma warning disable CA1707 // xunit test names use underscores for readability

using Valafind.Core.Embedding;
using Xunit;

namespace Valafind.Core.Tests.Embedding;

/// <summary>Exercises the tokenizer.json BPE implementation on a tiny synthetic vocabulary (no model needed).</summary>
public sealed class HfBpeTokenizerTests : IDisposable
{
    // Mirrors the structure of Gemma 3's tokenizer.json on a toy vocabulary.
    private const string TokenizerJson = """
    {
      "version": "1.0",
      "truncation": null,
      "padding": null,
      "added_tokens": [
        {"id": 0, "content": "<pad>", "single_word": false, "lstrip": false, "rstrip": false, "normalized": false, "special": true},
        {"id": 1, "content": "<eos>", "single_word": false, "lstrip": false, "rstrip": false, "normalized": false, "special": true},
        {"id": 2, "content": "<bos>", "single_word": false, "lstrip": false, "rstrip": false, "normalized": false, "special": true},
        {"id": 3, "content": "<unk>", "single_word": false, "lstrip": false, "rstrip": false, "normalized": false, "special": true},
        {"id": 13, "content": "\n", "single_word": false, "lstrip": false, "rstrip": false, "normalized": false, "special": false},
        {"id": 14, "content": "\n\n", "single_word": false, "lstrip": false, "rstrip": false, "normalized": false, "special": false},
        {"id": 15, "content": "<start>", "single_word": false, "lstrip": false, "rstrip": false, "normalized": false, "special": false}
      ],
      "normalizer": {"type": "Replace", "pattern": {"String": " "}, "content": "▁"},
      "pre_tokenizer": {"type": "Split", "pattern": {"String": " "}, "behavior": "MergedWithPrevious", "invert": false},
      "post_processor": {
        "type": "TemplateProcessing",
        "single": [{"SpecialToken": {"id": "<bos>", "type_id": 0}}, {"Sequence": {"id": "A", "type_id": 0}}, {"SpecialToken": {"id": "<eos>", "type_id": 0}}],
        "pair": [],
        "special_tokens": {"<bos>": {"id": "<bos>", "ids": [2], "tokens": ["<bos>"]}, "<eos>": {"id": "<eos>", "ids": [1], "tokens": ["<eos>"]}}
      },
      "decoder": null,
      "model": {
        "type": "BPE", "dropout": null, "unk_token": "<unk>", "continuing_subword_prefix": null, "end_of_word_suffix": null,
        "fuse_unk": true, "byte_fallback": true, "ignore_merges": false,
        "vocab": {
          "<pad>": 0, "<eos>": 1, "<bos>": 2, "<unk>": 3, "a": 4, "b": 5, "ab": 6, "▁": 7, "▁a": 8,
          "<0xC3>": 9, "<0xA9>": 10, "bb": 11, "abb": 12, "\n": 13, "\n\n": 14, "<start>": 15,
          "<0xF0>": 16, "<0x9F>": 17, "<0x98>": 18, "<0x80>": 19, "<": 20, "s": 21
        },
        "merges": [["a", "b"], ["b", "b"], ["▁", "a"], ["ab", "b"]]
      }
    }
    """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), "valafind-tok-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly HfBpeTokenizer _tokenizer;

    public HfBpeTokenizerTests()
    {
        File.WriteAllText(_path, TokenizerJson);
        _tokenizer = HfBpeTokenizer.Load(_path);
    }

    public void Dispose() => File.Delete(_path);

    private int[] Encode(string text)
    {
        var ids = new List<int>();
        _tokenizer.Encode(text, ids);
        return [.. ids];
    }

    [Fact]
    public void Special_ids_come_from_the_post_processor_and_vocab()
    {
        Assert.Equal(2, _tokenizer.BosId);
        Assert.Equal(1, _tokenizer.EosId);
        Assert.Equal(0, _tokenizer.PadId);
        Assert.Equal(7, _tokenizer.MaxTokenLength); // "<start>"
    }

    [Fact]
    public void Merges_apply_lowest_rank_first()
    {
        // a b b: (a,b) has rank 0 and wins over (b,b) rank 1, then (ab,b) rank 3 -> abb.
        Assert.Equal([12], Encode("abb"));
        // b b a b: (a,b) at pos 2 rank 0 first -> b b ab; then (b,b) -> bb ab.
        Assert.Equal([11, 6], Encode("bbab"));
    }

    [Fact]
    public void Spaces_are_normalised_to_U2581_without_a_dummy_prefix()
    {
        Assert.Equal([4, 8], Encode("a a"));
        Assert.Equal([8], Encode(" a"));
        Assert.Equal([7, 7], Encode("  "));
    }

    [Fact]
    public void Unknown_characters_use_byte_fallback_then_fused_unk()
    {
        Assert.Equal([9, 10], Encode("é"));
        Assert.Equal([16, 17, 18, 19], Encode("😀"));
        Assert.Equal([3], Encode("xyz")); // no byte tokens for ASCII x/y/z -> one fused <unk>
        Assert.Equal([4, 3, 4], Encode("axya"));
    }

    [Fact]
    public void Added_tokens_are_matched_leftmost_longest_on_raw_text()
    {
        Assert.Equal([4, 14, 13, 4], Encode("a\n\n\na"));
        Assert.Equal([15, 4], Encode("<start>a"));
        Assert.Equal([2, 4, 1], Encode("<bos>a<eos>"));
        Assert.Equal([20, 21, 3], Encode("<st")); // partial match is not a token
    }

    [Fact]
    public void Offsets_point_into_the_original_text()
    {
        var ids = new List<int>();
        var offsets = new List<int>();
        _tokenizer.Encode("ab a\né😀", ids, offsets);
        Assert.Equal([6, 8, 13, 9, 10, 16, 17, 18, 19], ids);
        Assert.Equal([0, 2, 4, 5, 5, 6, 6, 6, 6], offsets);
        Assert.Equal(ids.Count, _tokenizer.CountTokens("ab a\né😀"));
    }

    [Fact]
    public void Lone_surrogates_are_encoded_as_replacement_character()
    {
        // U+FFFD is not in the toy vocab and its bytes are not either -> <unk>.
        Assert.Equal([4, 3], Encode("a\uD800"));
    }

    [Fact]
    public void Empty_text_yields_no_tokens()
    {
        Assert.Empty(Encode(""));
        Assert.Equal(0, _tokenizer.CountTokens(""));
    }

    [Fact]
    public void Unsupported_tokenizer_features_are_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "valafind-tok-bad-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, TokenizerJson.Replace("\"type\": \"BPE\"", "\"type\": \"Unigram\"", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => HfBpeTokenizer.Load(path));

            File.WriteAllText(path, TokenizerJson.Replace("\"type\": \"Replace\"", "\"type\": \"NFKC\"", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => HfBpeTokenizer.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
