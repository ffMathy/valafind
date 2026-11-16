namespace Valafind.App.Platform;

/// <summary>Locates the files that make up the running app, in a way that also works for single-file publishes.</summary>
internal static class AppExecutable
{
    /// <summary>
    /// Path of the app's main assembly (<c>Valafind.dll</c>) when it exists as a separate file, i.e. when running as
    /// <c>dotnet Valafind.dll</c> during development; null for single-file and apphost builds. Deliberately not
    /// <c>Assembly.Location</c>, which is always empty for assemblies embedded in a single-file app.
    /// </summary>
    internal static string? EntryAssemblyPath
    {
        get
        {
            var name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
            if (string.IsNullOrEmpty(name))
                return null;
            var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            return File.Exists(path) ? path : null;
        }
    }
}
