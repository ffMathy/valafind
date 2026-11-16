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

    [JsonIgnore]
    public static string DefaultHotkey =>
        OperatingSystem.IsMacOS() ? "Alt+Space" :
        OperatingSystem.IsWindows() ? "Ctrl+Alt+Space" :
        "Super+Space";

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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
