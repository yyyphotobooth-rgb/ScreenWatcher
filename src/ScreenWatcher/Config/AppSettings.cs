using System.Text.Json;
using ScreenWatcher.Detection;

namespace ScreenWatcher.Config;

/// <summary>The user's own choices, kept in %LOCALAPPDATA%\ScreenWatcher\settings.json.
/// A missing or unreadable file means defaults (collection OFF, every working alert ON, no
/// Discord channel chosen). The bot token is NOT kept here (see <see cref="TokenStore"/>).</summary>
public sealed class AppSettings
{
    /// <summary>Old on/off "Collect data" setting (before collection modes). Kept so older
    /// settings files migrate (on → เปิดตลอด, off → ปิด); it follows <see cref="Collect"/>.</summary>
    public bool CollectData { get; set; }

    /// <summary>Collection mode as saved: "off", "auto" or "always"; null in a file written before
    /// modes existed (then <see cref="CollectData"/> decides).</summary>
    public string? CollectMode { get; set; }

    /// <summary>"เก็บภาพทุกครั้งที่เตือน": save the full frame + HUD crop of every alert (default off).</summary>
    public bool SaveAlertEvidence { get; set; }

    /// <summary>The collection mode in use (ปิด / อัตโนมัติ / เปิดตลอด).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CollectMode Collect
    {
        get => CollectModes.Parse(CollectMode) ?? (CollectData ? Config.CollectMode.Always : Config.CollectMode.Off);
        set
        {
            CollectMode = CollectModes.Name(value);
            CollectData = value == Config.CollectMode.Always;
        }
    }

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

    /// <summary>%LOCALAPPDATA%\ScreenWatcher, or the folder named by the SCREENWATCHER_DATA
    /// environment variable when it is set (to try a fresh install against an empty folder).</summary>
    public static string DataRoot { get; } =
        Environment.GetEnvironmentVariable("SCREENWATCHER_DATA") is { Length: > 0 } dir ? Path.GetFullPath(dir)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenWatcher");

    public static string DefaultPath => Path.Combine(DataRoot, "settings.json");
    public static string CollectRoot => Path.Combine(DataRoot, "collect");
    public static string EvidenceRoot => Path.Combine(DataRoot, "evidence");
    public static string JournalRoot => Path.Combine(DataRoot, "journal");
    public static string ExpTestRoot => Path.Combine(DataRoot, "exp-tests");

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

/// <summary>Collection mode: ปิด (off), อัตโนมัติ (auto: only while a rule is true), เปิดตลอด (always).</summary>
public enum CollectMode { Off, Auto, Always }

public static class CollectModes
{
    public static string Name(CollectMode m) => m switch { CollectMode.Auto => "auto", CollectMode.Always => "always", _ => "off" };

    public static CollectMode? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "off" => CollectMode.Off,
        "auto" => CollectMode.Auto,
        "always" => CollectMode.Always,
        _ => null,
    };

    public static string Thai(CollectMode m) => m switch { CollectMode.Auto => "อัตโนมัติ", CollectMode.Always => "เปิดตลอด", _ => "ปิด" };
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
    /// <summary>Sub-alert on the kill row: the EXP card left the buff bar.</summary>
    public const string ExpCard = "expCard";
    public const string InventoryFull = "inventoryFull";
    public const string LevelUp = "levelUp";
    public const string ExpStall = "expStall";
    public const string Unreadable = "unreadable";

    public static string For(WatchEvent e) => e.AlertId ?? e.Kind switch
    {
        WatchEventKind.Death => Death,
        WatchEventKind.GameDropped => GameDropped,
        _ => e.Kind.ToString(),
    };
}
