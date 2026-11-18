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

    /// <summary>Per-row alert threshold by alert id; missing = the row's default.</summary>
    public Dictionary<string, int> Thresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Potion rows where the user chose the RIGHT one of two same-type icons (default left).</summary>
    public Dictionary<string, bool> WatchRight { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
                s.Thresholds = new Dictionary<string, int>(s.Thresholds ?? [], StringComparer.OrdinalIgnoreCase);
                s.WatchRight = new Dictionary<string, bool>(s.WatchRight ?? [], StringComparer.OrdinalIgnoreCase);
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
    public const string GunAmmoEmpty = "gunAmmoEmpty";
    public const string MissileEmpty = "missileEmpty";
    public const string AmmoBox = "ammoBox";
    public const string FuelLow = "fuelLow";
    public const string FuelBottles = "fuelBottles";
    public const string MonsterCard = "monsterCard";
    public const string HpPotions = "hpPotions";
    public const string ShieldPotions = "shieldPotions";
    public const string SpPotions = "spPotions";
    public const string Kill300 = "kill300";

    public static string For(WatchEvent e) => e.AlertId ?? e.Kind switch
    {
        WatchEventKind.Death => Death,
        WatchEventKind.GameDropped => GameDropped,
        _ => e.Kind.ToString(),
    };
}
