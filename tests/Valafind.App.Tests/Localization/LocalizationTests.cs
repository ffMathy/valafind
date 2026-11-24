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

    /// <summary>The Microsoft Store lists the languages the package declares as the ones the app supports, and the
    /// Store listing (packaging/store/listing) has a text for each: a new UI language needs all three.</summary>
    [Fact]
    public void TheStorePackageAndListingCoverEveryLanguage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Valafind.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var store = Path.Combine(dir.FullName, "packaging", "store");
        var declared = ResourceLanguage().Matches(File.ReadAllText(Path.Combine(store, "windows", "AppxManifest.xml")))
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal("en-us", declared[0], ignoreCase: true);
        Assert.Equal(Loc.Languages.Count, declared.Count);
        foreach (var language in Loc.Languages)
        {
            var culture = CultureInfo.GetCultureInfo(language.Code);
            Assert.Single(declared, d => CultureInfo.GetCultureInfo(d) is var c
                && (c.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase) || IsUnder(c, culture)));
            Assert.True(File.Exists(Path.Combine(store, "listing", "text", language.Code + ".json")), $"no Store listing text for {language.Code}");
        }

        static bool IsUnder(CultureInfo specific, CultureInfo neutral)
        {
            for (var c = specific; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            {
                if (c.Name.Equals(neutral.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    [GeneratedRegex("<Resource Language=\"([^\"]+)\"")]
    private static partial Regex ResourceLanguage();

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
