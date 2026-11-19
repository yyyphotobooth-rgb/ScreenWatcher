using ScreenWatcher.Config;
using ScreenWatcher.Detection;
using ScreenWatcher.Detection.Hud;
using ScreenWatcher.Diagnostics;

namespace ScreenWatcher.Collection;

/// <summary>What the "อัตโนมัติ" rules look at on one poll (readings by alert row id).
/// <c>Unreadable</c>: the "อ่านค่าไม่ได้" row's state (rule g's full frames); null = not watched.</summary>
public readonly record struct AutoInputs(DateTime Now, HudScale Scale, bool HudPresent, Func<string, string> Reading, Func<string, int> Threshold,
    UnreadableDetector? Unreadable = null);

/// <summary>
/// Collection mode "อัตโนมัติ": decides, per collect region and per poll, whether the region is
/// collected and because of which rule. A region is active while a rule concerning it is true and
/// for PostSeconds after the rule was last true (instant rules f and h count as true on the poll
/// they happen). Rules (numbers in detectors.json "diagnostics.auto"):
///  fuel_low (a) fuel below FuelBelowPercent · hud_scale (b) scale ≥ ScaleAtLeast, all regions ·
///  kill_near (c) kill counter ≥ KillAtLeast · item_near (d) an item count ≤ threshold + ItemMargin ·
///  ammo_low (e) gun or missile below AmmoBelow · alert (f) any alert (HUD row: its region; death /
///  game dropped: all regions) · unknown (g) a row shows "?" longer than UnknownSeconds while the
///  HUD is present, or the row is over the "อ่านค่าไม่ได้" limit (that also saves the full client
///  frame, see <see cref="DataCollector"/>) · jump (h) a stable item / ammo count rises by more than ItemJump / AmmoJump, or
///  the stable kill counter goes down.
/// Runs on the polling thread; no allocation per poll.
/// </summary>
public sealed class AutoCollectRules
{
    public const string FuelLow = "fuel_low", HudScaleRule = "hud_scale", KillNear = "kill_near", ItemNear = "item_near",
        AmmoLow = "ammo_low", Alert = "alert", Unknown = "unknown", Jump = "jump";

    enum RowKind { Fuel, Kill, Item, Ammo, Status }

    sealed class Row(string id, RowKind kind, int region)
    {
        public readonly string Id = id;
        public readonly RowKind Kind = kind;
        public readonly int Region = region;
        public DateTime? UnknownSince;
        public int Candidate, Run;
        public int? Stable;
    }

    readonly AutoCollectConfig _c;
    readonly string[] _regions;
    readonly Row[] _rows;
    readonly TimeSpan _post;
    readonly DateTime[] _until;
    readonly string?[] _rule, _nowRule;
    readonly bool[] _active, _started, _stopped;
    readonly bool[] _alertRegion;
    bool _alertAll;

    public AutoCollectRules(WatcherConfig config, IReadOnlyList<string> regionNames)
    {
        _c = config.Diagnostics.Auto;
        _regions = regionNames.ToArray();
        _post = TimeSpan.FromSeconds(_c.PostSeconds);
        int n = _regions.Length;
        _until = new DateTime[n];
        _rule = new string?[n];
        _nowRule = new string?[n];
        _active = new bool[n];
        _started = new bool[n];
        _stopped = new bool[n];
        _alertRegion = new bool[n];

        var rows = new List<Row>
        {
            new(AlertIds.FuelLow, RowKind.Fuel, IndexOf(config.Fuel.CropRegion)),
            new(AlertIds.Kill300, RowKind.Kill, IndexOf(config.Kill.CropRegion)),
            new(AlertIds.GunAmmoEmpty, RowKind.Ammo, IndexOf(config.Ammo.CropRegion)),
            new(AlertIds.MissileEmpty, RowKind.Ammo, IndexOf(config.Ammo.CropRegion)),
        };
        foreach (var item in config.Items.Items) rows.Add(new(item.Id, RowKind.Item, IndexOf(config.Items.CropRegion)));
        // Message box: only rules f (its alert) and g ("?" = box not seen) apply.
        rows.Add(new(AlertIds.InventoryFull, RowKind.Status, IndexOf(config.Inventory.CropRegion)));
        rows.Add(new(AlertIds.LevelUp, RowKind.Status, IndexOf("level")));
        _rows = rows.ToArray();
    }

    /// <summary>Region index of a collect region name, or -1.</summary>
    public int IndexOf(string region) => Array.IndexOf(_regions, region);

    public IReadOnlyList<string> Regions => _regions;
    public bool IsActive(int region) => _active[region];
    /// <summary>The rule that (last) made the region active.</summary>
    public string? RuleOf(int region) => _rule[region];
    /// <summary>The region became active on the last <see cref="Evaluate"/>.</summary>
    public bool Started(int region) => _started[region];
    /// <summary>The region stopped being active on the last <see cref="Evaluate"/>.</summary>
    public bool Stopped(int region) => _stopped[region];

    /// <summary>An alert fired on this poll (call before <see cref="Evaluate"/>).</summary>
    public void NoteAlert(WatchEvent e)
    {
        if (e.Kind != WatchEventKind.Hud) { _alertAll = true; return; }
        int r = AlertRegion(e.AlertId);
        if (r >= 0) _alertRegion[r] = true;
        else _alertAll = true;
    }

    int AlertRegion(string? alertId)
    {
        foreach (var row in _rows) if (row.Id == alertId) return row.Region;
        return -1;
    }

    /// <summary>Evaluates every rule for one poll.</summary>
    public void Evaluate(in AutoInputs input)
    {
        Array.Clear(_nowRule);
        // Rules in order a..h: the first one true for a region names it.
        foreach (var row in _rows)
        {
            if (row.Region < 0) continue;
            string text = input.Reading(row.Id);
            bool hasValue = RowStats.TryValue(text, out int v);
            switch (row.Kind)
            {
                case RowKind.Fuel:
                    if (hasValue && text.StartsWith('~') && v < _c.FuelBelowPercent) Mark(row.Region, FuelLow);
                    break;
                case RowKind.Kill:
                    if (hasValue && v >= _c.KillAtLeast) Mark(row.Region, KillNear);
                    break;
                case RowKind.Item:
                    if (hasValue && v <= input.Threshold(row.Id) + _c.ItemMargin) Mark(row.Region, ItemNear);
                    break;
                case RowKind.Ammo:
                    if (hasValue && v < _c.AmmoBelow) Mark(row.Region, AmmoLow);
                    break;
            }
        }
        if (input.Scale.X >= _c.ScaleAtLeast || input.Scale.Y >= _c.ScaleAtLeast) MarkAll(HudScaleRule);
        if (_alertAll) MarkAll(Alert);
        for (int i = 0; i < _alertRegion.Length; i++) if (_alertRegion[i]) Mark(i, Alert);
        _alertAll = false;
        Array.Clear(_alertRegion);

        foreach (var row in _rows)
        {
            if (row.Region < 0) continue;
            string text = input.Reading(row.Id);
            // g: "?" for longer than UnknownSeconds while the HUD is present.
            if (input.HudPresent && text == RowReading.Unknown)
            {
                row.UnknownSince ??= input.Now;
                if ((input.Now - row.UnknownSince.Value).TotalSeconds > _c.UnknownSeconds) Mark(row.Region, Unknown);
            }
            else row.UnknownSince = null;
            if (input.Unreadable?.IsOverLimit(row.Id) == true) Mark(row.Region, Unknown);

            // h: implausible jump between stable reads.
            if (row.Kind == RowKind.Fuel || !int.TryParse(text, out int v)) { row.Run = 0; continue; }
            if (row.Run > 0 && v == row.Candidate) row.Run++;
            else { row.Candidate = v; row.Run = 1; }
            if (row.Run != Math.Max(1, _c.JumpStablePolls)) continue;
            if (row.Stable is { } prev && IsJump(row.Kind, prev, v)) Mark(row.Region, Jump);
            row.Stable = v;
        }

        for (int i = 0; i < _regions.Length; i++)
        {
            if (_nowRule[i] is { } r) { _until[i] = input.Now + _post; _rule[i] = r; }
            bool active = _nowRule[i] is not null || input.Now < _until[i];
            _started[i] = active && !_active[i];
            _stopped[i] = !active && _active[i];
            _active[i] = active;
        }
    }

    bool IsJump(RowKind kind, int previous, int current) => kind switch
    {
        RowKind.Kill => current < previous,
        RowKind.Item => current - previous > _c.ItemJump,
        RowKind.Ammo => current - previous > _c.AmmoJump,
        _ => false,
    };

    void Mark(int region, string rule) => _nowRule[region] ??= rule;

    void MarkAll(string rule)
    {
        for (int i = 0; i < _nowRule.Length; i++) _nowRule[i] ??= rule;
    }
}
