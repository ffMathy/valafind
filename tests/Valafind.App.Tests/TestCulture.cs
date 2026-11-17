using System.Globalization;
using System.Runtime.CompilerServices;

namespace Valafind.App.Tests;

/// <summary>Runs every test with English UI text and en-US formatting, whatever the machine's language, so the
/// assertions on English texts hold on localized CI agents and developer machines.</summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseEnglish()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}
