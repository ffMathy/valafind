using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valafind.Core.Configuration;

/// <summary>User settings, persisted as JSON in <see cref="AppPaths.SettingsFile"/>.</summary>
public sealed class ValafindSettings
{
    /// <summary>Global hotkey in "Modifier+Modifier+Key" form, e.g. "Ctrl+Alt+Space", "Alt+Space", "Super+Space".
    /// Null = platform default (see <see cref="DefaultHotkey"/>).</summary>
    public string? Hotkey { get; set; }

    /// <summary>Directories to index. Empty = the user's home directory.</summary>
    public List<string> Roots { get; set; } = [];

    /// <summary>Extra absolute paths or directory names to skip (in addition to built-in excludes).</summary>
    public List<string> Excludes { get; set; } = [];

    /// <summary>Index only while whole-machine CPU stays below this (percent).</summary>
    public double MaxCpuPercent { get; set; } = 30;

    /// <summary>Index only while disk busy time stays below this (percent).</summary>
    public double MaxDiskBusyPercent { get; set; } = 25;

    /// <summary>Pressure must stay below the thresholds this long before indexing (re)starts.</summary>
    public int CalmSeconds { get; set; } = 15;

    public bool PauseOnBattery { get; set; } = true;

    public bool UserPaused { get; set; }

    /// <summary>Matryoshka output size: 768, 512, 256 or 128. Changing it re-embeds everything.</summary>
    public int EmbeddingDimensions { get; set; } = 256;

    /// <summary>Directory holding EmbeddingGemma (tokenizer.model + onnx/model*.onnx). Null = auto-locate.</summary>
    public string? ModelDirectory { get; set; }

    /// <summary>Ctrl+Alt+Space (⌃⌥Space on macOS) everywhere. Deliberately not ⌥Space (macOS 15+ refuses Option-only
    /// hotkeys), Super+Space (GNOME's input-source switch), Alt+Space (Windows window menu, KRunner) or Cmd+Space (Spotlight).</summary>
    [JsonIgnore]
    public static string DefaultHotkey => "Ctrl+Alt+Space";

    [JsonIgnore]
    public string EffectiveHotkey => string.IsNullOrWhiteSpace(Hotkey) ? DefaultHotkey : Hotkey;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ValafindSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ValafindSettings>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings fall back to defaults; the file is rewritten on next save.
        }
        return new();
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        AppPaths.EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, AppPaths.PrivateFileOptions(FileMode.Create, FileAccess.Write, FileShare.None)))
        using (var writer = new StreamWriter(stream))
            writer.Write(JsonSerializer.Serialize(this, JsonOptions));
        AppPaths.TightenToOwner(tmp); // FileMode.Create keeps the mode of a leftover .tmp file.
        File.Move(tmp, path, overwrite: true);
    }
}
