using System.Text.Json;

namespace ScreenWatcher.Config;

/// <summary>The user's own choices, kept in %LOCALAPPDATA%\ScreenWatcher\settings.json.
/// A missing or unreadable file means defaults (collection OFF).</summary>
public sealed class AppSettings
{
    public bool CollectData { get; set; }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenWatcher");

    public static string DefaultPath => Path.Combine(DataRoot, "settings.json");
    public static string CollectRoot => Path.Combine(DataRoot, "collect");

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
