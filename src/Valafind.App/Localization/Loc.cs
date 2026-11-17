using System.Globalization;
using Avalonia.Media;

namespace Valafind.App.Localization;

/// <summary>A UI language Valafind ships a translation for.</summary>
/// <param name="Code">Culture name of the translation (the <c>Strings.&lt;code&gt;.resx</c> suffix), e.g. "de", "zh-Hans".</param>
/// <param name="NativeName">The language's name in that language, as shown in the language picker.</param>
public sealed record UiLanguage(string Code, string NativeName)
{
    /// <inheritdoc />
    public override string ToString() => NativeName;
}

/// <summary>
/// UI localization: the translated texts live in <c>Strings.resx</c> (English) and <c>Strings.&lt;culture&gt;.resx</c>,
/// and are read through the generated <see cref="Strings"/> class, which follows
/// <see cref="CultureInfo.CurrentUICulture"/>. The language is the operating system's unless the user picked one in
/// Settings (<see cref="Core.Configuration.ValafindSettings.Language"/>), applied once at start-up by <see cref="Apply"/>.
/// </summary>
public static class Loc
{
    /// <summary>Every shipped UI language, English (the source) first, then by number of speakers.</summary>
    public static IReadOnlyList<UiLanguage> Languages { get; } =
    [
        new("en", "English"),
        new("zh-Hans", "中文（简体）"),
        new("hi", "हिन्दी"),
        new("es", "Español"),
        new("ar", "العربية"),
        new("fr", "Français"),
        new("bn", "বাংলা"),
        new("pt", "Português"),
        new("ru", "Русский"),
        new("id", "Bahasa Indonesia"),
        new("ur", "اردو"),
        new("de", "Deutsch"),
        new("ja", "日本語"),
        new("mr", "मराठी"),
        new("vi", "Tiếng Việt"),
        new("te", "తెలుగు"),
        new("ha", "Hausa"),
        new("tr", "Türkçe"),
        new("ta", "தமிழ்"),
        new("ko", "한국어"),
    ];

    /// <summary>Formats a translated text with the current culture (numbers, dates inside the arguments).</summary>
    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>Splits a text around its single <c>{0}</c> placeholder, for sentences whose argument is styled
    /// differently (a bold hotkey). Without a placeholder the whole text comes before the argument.</summary>
    public static (string Before, string After) Split(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var at = format.IndexOf("{0}", StringComparison.Ordinal);
        return at < 0 ? (format, string.Empty) : (format[..at], format[(at + 3)..]);
    }

    /// <summary>
    /// Switches the UI language for the whole process. <paramref name="language"/> is a culture name such as "de" or
    /// "zh-Hans"; null, empty or an unknown name keeps the operating system's language. Only the UI culture changes:
    /// number and date formats keep following the user's regional settings, unless the system language is overridden,
    /// in which case they follow the chosen language too so a sentence does not mix conventions.
    /// </summary>
    /// <returns>The culture now used for UI text.</returns>
    public static CultureInfo Apply(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(language.Trim());
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                CultureInfo.CurrentUICulture = culture;
                var formatting = culture.IsNeutralCulture ? CultureInfo.CreateSpecificCulture(culture.Name) : culture;
                CultureInfo.DefaultThreadCurrentCulture = formatting;
                CultureInfo.CurrentCulture = formatting;
            }
            catch (CultureNotFoundException)
            {
                // Hand-edited settings or a runtime without ICU data: keep the system language.
            }
        }
        return CultureInfo.CurrentUICulture;
    }

    /// <summary>The shipped language matching <paramref name="culture"/> (or one of its parents), or null.</summary>
    public static UiLanguage? Match(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            var match = Languages.FirstOrDefault(l => string.Equals(l.Code, c.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }
        return null;
    }

    /// <summary>Layout direction of the current UI language (Arabic and Urdu read right to left).</summary>
    public static FlowDirection FlowDirection =>
        CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
}
