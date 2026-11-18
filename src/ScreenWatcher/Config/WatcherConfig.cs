using System.Text.Json;

namespace ScreenWatcher.Config;

/// <summary>All tunable values. Loaded from detectors.json (the single source of truth).</summary>
public sealed record WatcherConfig(int PollIntervalMs, WindowConfig Window, DropConfig Drop, HudScaleConfig HudScale, DeathConfig Death, CollectConfig Collect, AlertsConfig Alerts, AmmoConfig Ammo)
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

/// <summary>Discord alerts: which collect regions make up the death image, and how a failed send is retried.</summary>
public sealed record AlertsConfig(string[] DeathCropRegions, double[] RetryDelaysSeconds, int QueueLimit)
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
    CollectRegionConfig[] Regions);

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
