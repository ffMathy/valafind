using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Valafind.App.Localization;
using Xunit;

namespace Valafind.App.Tests.Localization;

public sealed partial class LocalizationTests
{
    private static readonly ResourceManager Resources = Strings.ResourceManager;

    public static TheoryData<string> Translations()
    {
        var data = new TheoryData<string>();
        foreach (var language in Loc.Languages.Where(l => l.Code != "en"))
            data.Add(language.Code);
        return data;
    }

    private static Dictionary<string, string> English() =>
        Resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    [Fact]
    public void ShipsTwentyOneLanguagesWithUniqueCodes()
    {
        Assert.Equal(21, Loc.Languages.Count);
        Assert.Equal(Loc.Languages.Count, Loc.Languages.Select(l => l.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("en", Loc.Languages[0].Code);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryTranslationCoversEveryTextWithTheSamePlaceholders(string code)
    {
        var culture = CultureInfo.GetCultureInfo(code);
        var translated = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(translated); // The satellite assembly is built and found.
        var texts = translated.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var english = English();

        Assert.Empty(english.Keys.Except(texts.Keys));
        Assert.Empty(texts.Keys.Except(english.Keys));
        foreach (var (key, source) in english)
        {
            var text = texts[key];
            Assert.False(string.IsNullOrWhiteSpace(text), $"{code}/{key} is empty");
            Assert.Equal(Placeholders(source), Placeholders(text));
            // Every text must survive string.Format with as many arguments as English uses.
            _ = string.Format(culture, text, Enumerable.Repeat<object>("x", 3).ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void TranslationsAreUsedForTheirCulture(string code)
    {
        var text = Resources.GetString(nameof(Strings.SettingsIndexedFolders), CultureInfo.GetCultureInfo(code));
        Assert.NotEqual(English()[nameof(Strings.SettingsIndexedFolders)], text);
    }

    [Fact]
    public void RegionalCulturesFallBackToTheirLanguage()
    {
        Assert.Equal(Resources.GetString(nameof(Strings.Save), CultureInfo.GetCultureInfo("de")),
            Resources.GetString(nameof(Strings.Save), CultureInfo.GetCultureInfo("de-AT")));
        Assert.Equal(Resources.GetString(nameof(Strings.Save), CultureInfo.GetCultureInfo("zh-Hans")),
            Resources.GetString(nameof(Strings.Save), CultureInfo.GetCultureInfo("zh-CN")));
        Assert.Equal("de", Loc.Match(CultureInfo.GetCultureInfo("de-CH"))?.Code);
        Assert.Null(Loc.Match(CultureInfo.GetCultureInfo("nl-NL")));
    }

    [Theory]
    [InlineData("Summon Valafind from anywhere with {0}.", "Summon Valafind from anywhere with ", ".")]
    [InlineData("{0} でどこからでも Valafind を呼び出せます。", "", " でどこからでも Valafind を呼び出せます。")]
    [InlineData("No placeholder", "No placeholder", "")]
    public void SplitCutsAroundThePlaceholder(string format, string before, string after) =>
        Assert.Equal((before, after), Loc.Split(format));

    [Fact]
    public void ApplyIgnoresUnknownLanguagesAndKeepsTheSystemCulture()
    {
        var before = CultureInfo.CurrentUICulture;
        Assert.Equal(before, Loc.Apply(null));
        Assert.Equal(before, Loc.Apply("  "));
        Assert.Equal(before, Loc.Apply("not a culture!"));
    }

    private static string[] Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
