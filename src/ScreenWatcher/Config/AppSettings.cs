using System.Text.Json;
using ScreenWatcher.Detection;

namespace ScreenWatcher.Config;

/// <summary>The user's own choices, kept in %LOCALAPPDATA%\ScreenWatcher\settings.json.
/// A missing or unreadable file means defaults (collection OFF, every working alert ON, no
/// Discord channel chosen). The bot token is NOT kept here (see <see cref="TokenStore"/>).</summary>
public sealed class AppSettings
{
    public bool CollectData { get; set; }

    /// <summary>The Discord server and text channel alerts go to (null = not chosen yet).</summary>
    public ulong? DiscordGuildId { get; set; }
    public ulong? DiscordChannelId { get; set; }
    /// <summary>"Server › #channel", shown before the bot has connected.</summary>
    public string? DiscordChannelLabel { get; set; }

    /// <summary>Per-alert on/off by alert id (see <see cref="AlertIds"/>); missing = on.</summary>
    public Dictionary<string, bool> AlertsEnabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAlertEnabled(string id) => AlertsEnabled.GetValueOrDefault(id, true);
    public void SetAlertEnabled(string id, bool on) => AlertsEnabled[id] = on;

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenWatcher");

    public static string DefaultPath => Path.Combine(DataRoot, "settings.json");
    public static string CollectRoot => Path.Combine(DataRoot, "collect");

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new();
                s.AlertsEnabled = new Dictionary<string, bool>(s.AlertsEnabled ?? [], StringComparer.OrdinalIgnoreCase);
                return s;
            }
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

/// <summary>Stable alert ids used in settings.json and the main window's rows.</summary>
public static class AlertIds
{
    public const string Death = "death";
    public const string GameDropped = "gameDropped";

    public static string For(WatchEventKind kind) => kind switch
    {
        WatchEventKind.Death => Death,
        WatchEventKind.GameDropped => GameDropped,
        _ => kind.ToString(),
    };
}
