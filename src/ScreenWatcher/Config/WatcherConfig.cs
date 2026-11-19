using System.Text.Json;

namespace ScreenWatcher.Config;

/// <summary>All tunable values. Loaded from detectors.json (the single source of truth).</summary>
public sealed record WatcherConfig(int PollIntervalMs, WindowConfig Window, DropConfig Drop, HudScaleConfig HudScale, DeathConfig Death, CollectConfig Collect, AlertsConfig Alerts, AmmoConfig Ammo, FuelConfig Fuel, KillConfig Kill, ItemsConfig Items, DiagnosticsConfig Diagnostics, UnreadableConfig Unreadable, QuestPanelConfig QuestPanel, InventoryConfig Inventory, LevelConfig Level)
{
    public const string FileName = "detectors.json";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        RespectRequiredConstructorParameters = true,
    };

    public static WatcherConfig Load(string path) =>
        JsonSerializer.Deserialize<WatcherConfig>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Empty config: {path}");
}

public sealed record WindowConfig(string TitlePrefix, string[] ExcludeTitles);

public sealed record DropConfig(int MissingChecksToFire, string Message);

/// <summary>Discord alerts: how a failed send is retried.</summary>
public sealed record AlertsConfig(double[] RetryDelaysSeconds, int QueueLimit)
{
    public IReadOnlyList<TimeSpan> RetryDelays => RetryDelaysSeconds.Select(TimeSpan.FromSeconds).ToArray();
}

/// <summary>Gauge outer-ring locator. Ring geometry is in the 1.0 layout, continuous client
/// coordinates (pixel i spans [i, i+1)).</summary>
public sealed record HudScaleConfig(
    double[] RingCenter,
    double RingOuterRadius,
    double RingInnerRadius,
    double EdgeProbePx,
    int Angles,
    double MinScale,
    double MaxScale,
    double CoarseStep,
    double RefineRange,
    double RefineStep,
    double MinScore,
    double MinPeakToMean,
    int RetryEveryPolls);

public sealed record DeathConfig(string Message, double HoldSeconds, GaugeConfig Gauge, DialogConfig Dialog);

/// <summary>Patch points are patch centres in client coordinates, as [x, y].</summary>
public sealed record GaugeConfig(
    int PatchSize,
    int[][] LeftPatches,
    int[][] RightPatches,
    int RedMinR,
    double RedMaxOtherRatio,
    int DarkMaxChannel,
    int MinHpFilledPatches);

/// <summary>Patch points are patch centres as [dx, dy] offsets from the client centre.</summary>
public sealed record DialogConfig(
    int PatchWidth,
    int PatchHeight,
    int[][] PatchOffsetsFromCenter,
    int NavyMaxR,
    int NavyMinB,
    int NavyMaxB,
    int NavyMinBMinusR,
    int MinMatchingPatches);

/// <summary>"Collect data" mode: HUD crops and full frames saved on this PC for later study.</summary>
public sealed record CollectConfig(
    double MaxMegabytes,
    double MinSecondsBetweenSaves,
    double HeartbeatSeconds,
    double FullFrameMinutes,
    int QueueLimit,
    ChangeConfig Change,
    CollectRegionConfig[] Regions,
    CollectRegionConfig[]? UnbufferedRegions = null)
{
    /// <summary>Regions collected and used for alert crops: Regions (also kept in the in-memory
    /// HUD ring) then UnbufferedRegions (not in the ring, which has an 8 MB budget).</summary>
    public IEnumerable<CollectRegionConfig> AllRegions => Regions.Concat(UnbufferedRegions ?? []);
}

/// <summary>Collect change test, see Collection/HudSignature.cs. A region changed when at least
/// MinChangedPixels (at 1.0 scale; scaled by area) changed HUD colour-key class versus the last
/// saved crop, at the best alignment within +-MaxShift pixels.</summary>
public sealed record ChangeConfig(int MaxShift, double MinChangedPixels);

/// <summary>A HUD colour: R, G, B each within [Min, Max]. With OutlineMax &gt;= 0 the pixel must
/// also have a 3x3 neighbour whose R, G and B are all &lt;= OutlineMax (outlined text). With
/// FlatRadius &gt; 0 the pixels FlatRadius to the left and right must have the same colour
/// (+-FlatTolerance per channel): flat panel fills, unlike textured scenery.</summary>
public sealed record ColourKey(string Name, int[] Min, int[] Max, int OutlineMax = -1, int FlatRadius = 0, int FlatTolerance = 2);

/// <summary>Rect = [x, y, w, h] in the 1.0 layout, offset from the anchor point and multiplied by
/// the measured HUD scale. Anchors: topLeft (0,0), topRight (W,0), bottomCenter (W/2,H),
/// center (W/2,H/2). Keys = the HUD colours whose layout the change test compares.
/// MinChangedPixels overrides the global change threshold for this region.</summary>
public sealed record CollectRegionConfig(string Name, string Anchor, int[] Rect, ColourKey[] Keys, double? MinChangedPixels = null);

/// <summary>A number drawn in fixed-pitch cells. CellX = left edge of each digit cell and Y = the
/// cells' top, in the 1.0 layout relative to the anchor (topLeft / topRight / bottomCenter);
/// the cell size is the glyph size of the named font in glyphs.json. Inks = the digit colours to
/// try, in order (Detection/Hud/Ink.cs). The field is searched
/// +-SearchPx native pixels. A digit is accepted when its match is at least MinScore and leads the
/// next best digit by MinMargin; a cell with less ink than BlankInk is blank (allowed only in
/// front of the number, and only with AllowBlank).</summary>
public sealed record DigitFieldConfig(
    string Anchor, double[] CellX, double Y, string Glyphs, string[] Inks,
    int SearchPx, double MinScore, double MinMargin, bool AllowBlank = false, double BlankInk = 4);

/// <summary>Ammo counters (top-left strip). An alert fires when a counter reads 00000 on
/// StablePolls consecutive polls and re-arms when it reads above 0. CropRegion = the collect
/// region sent with the alert.</summary>
public sealed record AmmoConfig(int StablePolls, string CropRegion, AmmoCounterConfig Gun, AmmoCounterConfig Missile);

public sealed record AmmoCounterConfig(string Message, DigitFieldConfig Field);

/// <summary>Fuel arc on the gauge's outer ring (Detection/Hud/FuelArc.cs). Angles are degrees
/// clockwise from the top of the ring; Center and Radii are 1.0-layout continuous coordinates.
/// The arc is Segments pieces of SegmentDeg, the first ending at PumpEndDeg. A probe is "lit"
/// (orange, any of the brightness steps) or the arc's own "dark" brown; a segment needs
/// MinVoteShare of its probes to agree. Lit segments n stand for a fuel % in
/// (PercentAtNoSegments + (n-1) * PercentPerSegment, PercentAtNoSegments + n * PercentPerSegment].</summary>
public sealed record FuelConfig(
    string Message, string CropRegion, int StablePolls, int DefaultThresholdPercent, int RearmMarginPercent,
    double[] Center, double[] Radii, int Segments, double SegmentDeg, double PumpEndDeg, double PumpCapDeg, double TopClearDeg,
    int ProbesPerSegment, double MinVoteShare,
    int LitMinR, int LitMaxB, double HueMinGOverR, double HueMaxGOverR,
    int[] DarkR, int[] DarkG, int DarkMaxB,
    double PercentAtNoSegments, double PercentPerSegment);

/// <summary>Kill counter (orange number, right edge). The alert fires when the counter reads
/// Target, or when the panel disappears while the last confirmed value was at least NearTarget.
/// A value is confirmed when it equals the previous poll's reading or continues it upwards by at
/// most MaxStepPerPoll (the counter can change every second in combat); "gone" needs StablePolls
/// polls with no digits. Re-arms when a confirmed value is below RearmBelow.</summary>
public sealed record KillConfig(
    string Message, string CropRegion, int StablePolls, int Target, int NearTarget, int RearmBelow, int MaxStepPerPoll, DigitFieldConfig Field);

/// <summary>Item bar (Detection/Hud/ItemBar.cs). Slot k's frame starts at
/// (W/2 + SlotLeft + SlotPitch * k, H + SlotTop) in the 1.0 layout; its interior is 28x28, one
/// pixel in. Icons are looked for +-SearchPx around that (SearchPxScaled on a stretched HUD) and
/// accepted when the mean colour difference over IconRows cells ([rowFrom, rowTo, colFrom, colTo]
/// of the 14x14 grid) is at most IconMaxDistance. Potion kits: MarkCells ([rowFrom, rowTo, colFrom,
/// colTo]) decide the type (see ItemBarReader); a kit-like slot up to KitUncertainDistance is
/// "uncertain". Count digits: pure white (>= CountWhiteMin)
/// pixels whose four neighbours are white or outline (<= CountDarkMax), in a box CountBoxWidth
/// left of the icon's right edge; CountGlyphs holds the ten 8-row digit bitmaps seen at 1.0, and
/// CountFontFace at character heights CountFontMinHeight..MaxHeight is rendered for other scales.
/// The bar counts as visible when an icon was found or FrameMinRuns runs of FrameMinRun
/// frame-blue pixels lie on one row at the slots' top edge.</summary>
public sealed record ItemsConfig(
    int StablePolls, int GoneStablePolls, string CropRegion, string LowMessage, string GoneMessage,
    int Slots, double SlotLeft, double SlotPitch, double SlotTop, int SearchPx, int SearchPxScaled,
    double IconMaxDistance, int[][] IconRows, int[] MarkCells, double MarkMaxDistance, double MarkMinMargin, double KitUncertainDistance,
    int CountBoxWidth, int CountBoxHeight, int CountWhiteMin, int CountDarkMax, Dictionary<string, string[]> CountGlyphs,
    string CountFontFace, int CountFontMinHeight, int CountFontMaxHeight,
    int FrameMaxR, int[] FrameG, int[] FrameB, int FrameMinRun, int FrameMinRuns,
    ItemDef[] Items);

/// <summary>A watched item: alert row id, Thai name and unit, default threshold, the icon
/// pictures (icons.json) that show it, for potions the kit picture whose mark it has, and
/// optionally its own icon distance limit (else ItemsConfig.IconMaxDistance) and its own alert texts
/// (else ItemsConfig.LowMessage / GoneMessage; {0} name, {1} count, {2} unit).</summary>
public sealed record ItemDef(string Id, string Name, string Unit, int DefaultThreshold, string[] Icons, string? Mark = null, double? MaxDistance = null,
    string? LowMessage = null, string? GoneMessage = null);

/// <summary>Field-trial diagnostics (Diagnostics/): event journal, alert evidence, /miss, the
/// in-memory ring of HUD crops and the "อัตโนมัติ" collection rules.</summary>
public sealed record DiagnosticsConfig(
    double JournalMaxMegabytes, double EvidenceMaxMegabytes, int EvidenceJpegQuality, int RingSeconds, int MissNoteMaxChars,
    AutoCollectConfig Auto);

/// <summary>Collection mode "อัตโนมัติ" rules (see Collection/AutoCollectRules.cs).</summary>
public sealed record AutoCollectConfig(
    double PreSeconds, double PostSeconds,
    double FuelBelowPercent, double ScaleAtLeast, int KillAtLeast, int ItemMargin, int AmmoBelow,
    double UnknownSeconds, int ItemJump, int AmmoJump, int JumpStablePolls, double UnreadableFullFrameRepeatSeconds);

/// <summary>Row "อ่านค่าไม่ได้เกิน N วินาที" (Detection/Hud/UnreadableDetector.cs): default N, how long
/// after the first row other rows may join the same message, the message ({0} = short names) and
/// the watched rows in table order as [alert row id, short name].</summary>
public sealed record UnreadableConfig(int DefaultSeconds, double CombineSeconds, string Message, string[][] Rows);

/// <summary>Level-up quest panel (Detection/Hud/QuestPanel.cs). CheckY = top of the check glyph's
/// box in the 1.0 layout (searched +-SearchY rows, and along x from SearchFromWidth of the client
/// width); the X glyph's box is XOffset to the right. Masks: '#' glyph, '.' band, other = either.
/// A glyph cell is "on" when its luminance is at least BrightDelta above the mean of the box's
/// border; a glyph matches when MinMatch (MinMatchScaled on a stretched HUD) of its cells (and of
/// its '#' cells) agree. The portrait
/// box's bottom border: rows LineDy / LineDy+1 below the check box top are brighter by
/// LineMinContrast than the two rows above, over LineMinShare of the columns LineDx[0]..LineDx[1]
/// (relative to the check box). Present on more than the "อ่านค่าไม่ได้" limit -> Message (once per
/// appearance; gone after GoneStablePolls polls) instead of the generic message for CoveredRows.</summary>
public sealed record QuestPanelConfig(
    string Message, string[] CoveredRows, int GoneStablePolls,
    double CheckY, int SearchY, double SearchFromWidth, double XOffset, int BrightDelta, double MinMatch, double MinMatchScaled,
    double LineDy, double[] LineDx, int LineMinContrast, double LineMinShare,
    string[] CheckMask, string[] CloseMask);

/// <summary>Row "ช่องเก็บของเต็ม" (Detection/Hud/InventoryDetector.cs, SystemMessage.cs). TextArea and
/// TitleAt are bottomRight-anchored 1.0-layout offsets ([x, y, w, h] / [x, y]); colours are RGB
/// boxes; the phrase is rendered in FontFace at FontMinHeight..FontMaxHeight and may differ from
/// the yellow pixels in at most MaxMismatch of its box.</summary>
public sealed record InventoryConfig(
    string Message, string Phrase, string FontFace, int FontMinHeight, int FontMaxHeight, string OkText, string FullText, string CropRegion,
    int[] TextArea, int[] YellowMin, int[] YellowMax, int[] GreenMin, int[] GreenMax, double MaxMismatch, int TextMinPixels,
    int[] TitleAt, int TitleSearch, int TitleDelta, double TitleMinMatch, double TitleMinMatchScaled, string[] TitleMask, int MissingPolls);

/// <summary>Row "เลเวลอัพ" (Detection/Hud/LevelDetector.cs). Area = bottomCenter [x, y, w, h] in the
/// 1.0 layout; Prefix = "Lv." (7 rows), Digits = 5x7 cells ('#' ink); a cell matches with at least
/// MinScore (MinScoreScaled on a stretched HUD) of its pixels and MinMargin over the next digit
/// (fallback). Primary: FontFace digits at FontMinHeight..FontMaxHeight, at most MaxDigitGap blank
/// columns between the level digits.</summary>
public sealed record LevelConfig(
    string Message, int StablePolls, int MaxStep, double QuestQuietSeconds, int[] Area, int InkMin, int InkMaxSpread,
    double MinScore, double MinScoreScaled, double MinMargin, int DigitStart, int DigitPitch, int MaxDigits,
    string[] Prefix, Dictionary<string, string[]> Digits, string FontFace, int FontMinHeight, int FontMaxHeight, int MaxDigitGap);
