using System.Drawing;
using ScreenWatcher.Collection;
using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection.Hud;

/// <summary>
/// Item rows (ammo box, fuel bottles, monster card, the three potions). Per item:
/// - "เหลือ N": the count read on StablePolls polls in a row is at or below the row threshold.
///   Fires once; re-arms when a stable count is above the threshold again.
/// - "หมด": the icon was seen earlier (with a readable count) and is now missing for GoneStablePolls polls while the bar
///   itself is visible. Fires once; re-arms when the icon is seen again.
/// Potions: when two icons of one type are on the bar, their slots are remembered and only the
/// side chosen in the row (left by default) is watched. If that one runs out the row does not
/// move to the other icon by itself.
/// </summary>
public sealed class ItemDetector : IHudDetector, IArmedState
{
    sealed class State(ItemDef def, int stablePolls)
    {
        public readonly ItemDef Def = def;
        public readonly string GoneKey = def.Id + ":gone";
        public readonly StableValue<int> Count = new(stablePolls);
        public bool Seen, ArmedLow = true, GoneFired;
        public int PresentRun, AbsentRun, HiddenRun;
        public int? SlotLeft, SlotRight;
    }

    public const string GoneText = "หมด";

    readonly ItemsConfig _c;
    readonly ItemBarReader _reader;
    readonly CollectRegion _crop;
    readonly HudReadings _readings;
    readonly HudOptions _options;
    readonly State[] _states;
    readonly ItemBarReading _reading = new();
    readonly List<FoundItem> _mine = [];

    public ItemDetector(WatcherConfig config, IconSet icons, HudReadings readings, HudOptions options, CountFont? font = null)
    {
        _c = config.Items;
        _reader = new ItemBarReader(_c, icons, font);
        _crop = HudCrop.Region(config, _c.CropRegion);
        _readings = readings;
        _options = options;
        _states = _c.Items.Select(d => new State(d, _c.StablePolls)).ToArray();
    }

    public ItemBarReader Reader => _reader;

    public void Process(Frame frame, HudScale scale, DateTime now, List<WatchEvent> events)
    {
        _reader.Read(frame, scale, _reading);
        Update(_reading, now, _crop.Locate(frame.Width, frame.Height, scale), events);
    }

    /// <summary>Alert logic for one poll's bar reading (also called directly by tests).</summary>
    public void Update(ItemBarReading r, DateTime now, Rectangle crop, List<WatchEvent> events)
    {
        foreach (var st in _states)
        {
            var def = st.Def;
            _mine.Clear();
            foreach (var f in r.Found) if (f.ItemId == def.Id) _mine.Add(f);   // already left to right

            FoundItem? watched = _mine.Count > 0 ? _mine[0] : null;
            if (def.Mark is not null)
            {
                if (_mine.Count >= 2) { st.SlotLeft = _mine[0].Slot; st.SlotRight = _mine[^1].Slot; }
                else if (_mine.Count == 1 && st.SlotLeft is not null && _mine[0].Slot != st.SlotLeft && _mine[0].Slot != st.SlotRight)
                    st.SlotLeft = st.SlotRight = null;      // the remaining icon sits somewhere new: one potion again
                if (st.SlotLeft is not null)
                {
                    int want = (_options.WatchRight(def.Id) ? st.SlotRight : st.SlotLeft)!.Value;
                    watched = null;
                    foreach (var f in _mine) if (f.Slot == want) watched = f;
                }
            }
            bool two = st.SlotLeft is not null;
            int threshold = _options.Threshold(def.Id, def.DefaultThreshold);

            if (watched is { } w)
            {
                st.AbsentRun = st.HiddenRun = 0;
                st.PresentRun++;
                if (st.Count.Push(w.Count))
                {
                    // "Seen" needs the icon AND a readable count on consecutive polls: a slot that
                    // only looks like an icon (scenery behind an empty slot) has no digits.
                    st.Seen = true;
                    st.GoneFired = false;
                    int v = st.Count.Value!.Value;
                    if (v > threshold) st.ArmedLow = true;
                    else if (st.ArmedLow)
                    {
                        st.ArmedLow = false;
                        events.Add(new WatchEvent(WatchEventKind.Hud, string.Format(_c.LowMessage, Spaced(def.Name), v, def.Unit), now, def.Id, crop));
                    }
                }
                _readings.Set(def.Id, st.Count.Latest?.ToString() ?? RowReading.Unknown, st.Count.Value <= threshold, two);
            }
            else if (r.BarVisible)
            {
                st.PresentRun = st.HiddenRun = 0;
                st.Count.Reset();
                if (++st.AbsentRun >= _c.GoneStablePolls)
                {
                    if (st.Seen && !st.GoneFired)
                    {
                        st.GoneFired = true;
                        events.Add(new WatchEvent(WatchEventKind.Hud, string.Format(_c.GoneMessage, Spaced(def.Name)), now, def.Id, crop));
                    }
                    _readings.Set(def.Id, st.GoneFired ? GoneText : RowReading.None, st.GoneFired, two);
                }
            }
            else
            {
                // Bar not on screen: nothing can be said about the item.
                st.PresentRun = st.AbsentRun = 0;
                st.Count.Reset();
                if (++st.HiddenRun >= StableValue<int>.UnknownAfter) _readings.Set(def.Id, RowReading.None, false, two);
            }
        }
    }

    /// <summary>Thai words follow each other without a space, but a name ending in Latin letters
    /// ("ยา SP") needs one before the next word.</summary>
    static string Spaced(string name) => char.IsAsciiLetterOrDigit(name[^1]) ? name + " " : name;

    public void CopyArmed(IDictionary<string, bool> into)
    {
        foreach (var st in _states)
        {
            into[st.Def.Id] = st.ArmedLow;
            into[st.GoneKey] = !st.GoneFired;
        }
    }

    public void Reset()
    {
        foreach (var st in _states)
        {
            st.Count.Reset();
            st.PresentRun = st.AbsentRun = st.HiddenRun = 0;
            _readings.Set(st.Def.Id, RowReading.None, false, st.SlotLeft is not null);
        }
    }
}
