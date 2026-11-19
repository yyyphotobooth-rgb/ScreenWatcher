using System.Globalization;
using System.Text.Json;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.ExpTest;

/// <summary>What is kept of one result (exp-tests\{id}.json).</summary>
public sealed record ExpRecord(
    string Id, string Source, int Minutes, DateTime StartTime, int StartLevel, int StartPct,
    DateTime? EndTime, int? EndLevel, int? EndPct, int? GainHundredths, double? GainPer10Min, int? Kills,
    string[] Flags, bool Usable, bool Comparable, string? MapCompare, string Text)
{
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

    /// <summary>The last <paramref name="count"/> comparable results (by start time), sorted by gain
    /// per 10 minutes, best first.</summary>
    public IReadOnlyList<ExpListRow> LastComparable(int count) =>
        [.. LoadAll().Where(r => r.Comparable && r.GainPer10Min is not null)
            .OrderByDescending(r => r.StartTime).Take(count)
            .OrderByDescending(r => r.GainPer10Min)
            .Select(r => new ExpListRow(r, ReadMap(r.Id)))];

    byte[]? ReadMap(string id)
    {
        try { string p = Path.Combine(root, id + "_map.png"); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
        catch (IOException) { return null; }
    }

    public static string NewId(DateTime t) => t.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
}
