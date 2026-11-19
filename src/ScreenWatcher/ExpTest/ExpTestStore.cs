using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.ExpTest;

/// <summary>What is kept of one result (exp-tests\{id}.json).</summary>
public sealed record ExpRecord(
    string Id, string Source, int Minutes, DateTime StartTime, int StartLevel, int StartPct,
    DateTime? EndTime, int? EndLevel, int? EndPct, int? GainHundredths, double? GainPer10Min, int? Kills,
    string[] Flags, bool Usable, bool Comparable, string? MapCompare, string Text)
{
    // Derived values: results saved before the per-minute change (and the zone rule) load and display too.
    [JsonIgnore] public double? Elapsed => EndTime is { } e ? (e - StartTime).TotalMinutes : null;
    [JsonIgnore] public bool LevelUp => Flags.Contains("มีเลเวลอัพ");
    [JsonIgnore] public double? RatePerMin => GainHundredths is { } g && Elapsed is { } m ? ExpMath.Rate(g, m) : null;
    [JsonIgnore] public double? KillsPerMin => Kills is { } k && Elapsed is { } m && m > 0 ? k / m : null;
    [JsonIgnore] public int? MinutesPerLevel => RatePerMin is { } r && !LevelUp ? ExpMath.MinutesPerLevel(r) : null;

    /// <summary>Comparable by the current rules: a map change counts only when it was the NAME (an old
    /// record whose only difference was the zone counts as comparable).</summary>
    [JsonIgnore]
    public bool ComparableNow => Usable && !Flags.Contains("เกมหลุด") && !Flags.Contains("หยุดเฝ้าระหว่างทดสอบ")
        && !(Flags.Contains("เปลี่ยนแมป") && MapCompare?.StartsWith("zone ", StringComparison.Ordinal) != true);

    /// <summary>Flags to show: an old "map changed" caused by the zone alone is no longer a flag.</summary>
    [JsonIgnore]
    public string[] ShownFlags => [.. Flags.Where(f => f != "เปลี่ยนแมป" || MapCompare?.StartsWith("zone ", StringComparison.Ordinal) != true)];

    public static ExpRecord From(ExpResult r) => new(
        r.Id, r.Source, r.Minutes, r.Start.Time, r.Start.Level, r.Start.PctHundredths,
        r.End?.Time, r.End?.Level, r.End?.PctHundredths, r.GainHundredths, r.GainPer10Min, r.Kills,
        [.. r.Flags.Words()], r.Usable, r.Comparable,
        r.Map is { } m ? $"{m.Why} name {m.NameDiff} zone {m.ZoneDiff}" : null, r.Text);
}

public sealed record ExpListRow(ExpRecord Data, byte[]? MapPng);

/// <summary>
/// Results under %LOCALAPPDATA%\ScreenWatcher\exp-tests: {id}.json (data), {id}.png (the result
/// image) and {id}_map.png (the start map label, used by /exp list). Never deletes anything.
/// </summary>
public sealed class ExpTestStore(string root)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root => root;

    /// <summary>Writes the three files; returns the data file's path (null when writing failed).</summary>
    public string? Save(ExpResult r, byte[] resultPng)
    {
        try
        {
            Directory.CreateDirectory(root);
            string stem = Path.Combine(root, r.Id);
            File.WriteAllBytes(stem + ".png", resultPng);
            if (r.Start.Map?.Composite() is { IsEmpty: false } map) File.WriteAllBytes(stem + "_map.png", PngEncoder.EncodeBgra(map.Pixels, map.Width, map.Height));
            File.WriteAllText(stem + ".json", JsonSerializer.Serialize(ExpRecord.From(r), Json));
            return stem + ".json";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<ExpRecord> LoadAll()
    {
        var list = new List<ExpRecord>();
        if (!Directory.Exists(root)) return list;
        foreach (var f in Directory.GetFiles(root, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<ExpRecord>(File.ReadAllText(f), Json) is { } r) list.Add(r); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>The last <paramref name="count"/> comparable results (by start time), sorted by EXP per
    /// minute, best first.</summary>
    public IReadOnlyList<ExpListRow> LastComparable(int count) =>
        [.. LoadAll().Where(r => r.ComparableNow && r.RatePerMin is not null)
            .OrderByDescending(r => r.StartTime).Take(count)
            .OrderByDescending(r => r.RatePerMin)
            .Select(r => new ExpListRow(r, ReadMap(r.Id)))];

    byte[]? ReadMap(string id)
    {
        try { string p = Path.Combine(root, id + "_map.png"); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
        catch (IOException) { return null; }
    }

    public static string NewId(DateTime t) => t.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
}
