namespace Valafind.Core.Embedding;

/// <summary>EmbeddingGemma task prompts (see the model card, "Prompt instructions").</summary>
internal static class EmbeddingPrompts
{
    public const string QueryPrefix = "task: search result | query: ";
    public const string DocumentTitlePrefix = "title: ";
    public const string DocumentTextSeparator = " | text: ";
    public const string NoTitle = "none";

    public static string Query(string query) => QueryPrefix + query;

    public static string Document(string? title, string text) =>
        DocumentTitlePrefix + NormalizeTitle(title) + DocumentTextSeparator + text;

    /// <summary>Blank titles become "none"; line breaks and tabs are flattened so the prompt keeps its shape.</summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return NoTitle;
        var t = title.Trim();
        return t.AsSpan().IndexOfAny('\r', '\n', '\t') < 0 ? t : t.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }
}
