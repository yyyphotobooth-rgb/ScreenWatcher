using System.Text.Json;

namespace ScreenWatcher.Config;

/// <summary>All tunable values. Loaded from detectors.json (the single source of truth).</summary>
public sealed record WatcherConfig(int PollIntervalMs, WindowConfig Window, DropConfig Drop, HudScaleConfig HudScale, DeathConfig Death)
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
