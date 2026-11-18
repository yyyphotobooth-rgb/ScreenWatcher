using ScreenWatcher.Config;
using ScreenWatcher.Imaging;

namespace ScreenWatcher.Detection;

/// <summary>What one frame says about the death state.</summary>
/// <param name="GaugeDead">Both inner halves of the HP gauge are dark.</param>
/// <param name="HpFilled">The HP (left, red) half shows fill again.</param>
/// <param name="DialogPresent">The navy "return to town?" box is at the client centre.</param>
public readonly record struct DeathObservation(bool GaugeDead, bool HpFilled, bool DialogPresent);

/// <summary>Turns a frame into a <see cref="DeathObservation"/> using colour checks on small patches.</summary>
public sealed class DeathFrameAnalyzer(DeathConfig config)
{
    public DeathObservation Analyze(Frame frame) => Analyze(frame, HudScale.One);

    /// <summary>Config coordinates are the 1.0 HUD layout; <paramref name="scale"/> maps them onto
    /// this frame (gauge about the client origin, dialog about the client centre).</summary>
    public DeathObservation Analyze(Frame frame, HudScale scale)
    {
        var g = config.Gauge;
        int pw = scale.SizeX(g.PatchSize), ph = scale.SizeY(g.PatchSize);
        int leftDark = 0, leftRed = 0, rightDark = 0;
        foreach (var p in g.LeftPatches)
        {
            var c = frame.MeanAround(scale.MapX(p[0]), scale.MapY(p[1]), pw, ph);
            if (c is not { } rgb) continue;
            if (IsDark(rgb, g)) leftDark++;
            if (IsRed(rgb, g)) leftRed++;
        }
        foreach (var p in g.RightPatches)
        {
            var c = frame.MeanAround(scale.MapX(p[0]), scale.MapY(p[1]), pw, ph);
            if (c is { } rgb && IsDark(rgb, g)) rightDark++;
        }

        bool gaugeDead = leftDark == g.LeftPatches.Length && rightDark == g.RightPatches.Length;
        bool hpFilled = leftRed >= g.MinHpFilledPatches;
        return new DeathObservation(gaugeDead, hpFilled, DialogPresent(frame, scale));
    }

    public bool DialogPresent(Frame frame) => DialogPresent(frame, HudScale.One);

    public bool DialogPresent(Frame frame, HudScale scale)
    {
        var d = config.Dialog;
        int cx = frame.Width / 2, cy = frame.Height / 2, matches = 0;
        int pw = scale.SizeX(d.PatchWidth), ph = scale.SizeY(d.PatchHeight);
        foreach (var o in d.PatchOffsetsFromCenter)
        {
            var c = frame.MeanAround(cx + (int)Math.Round(o[0] * scale.X), cy + (int)Math.Round(o[1] * scale.Y), pw, ph);
            if (c is { } rgb && IsNavy(rgb, d)) matches++;
        }
        return matches >= d.MinMatchingPatches;
    }

    public static bool IsRed(Rgb c, GaugeConfig g) =>
        c.R >= g.RedMinR && c.G <= c.R * g.RedMaxOtherRatio && c.B <= c.R * g.RedMaxOtherRatio;

    public static bool IsDark(Rgb c, GaugeConfig g) => c.Max <= g.DarkMaxChannel;

    public static bool IsNavy(Rgb c, DialogConfig d) =>
        c.R <= d.NavyMaxR && c.B >= d.NavyMinB && c.B <= d.NavyMaxB && c.B >= c.G && c.B - c.R >= d.NavyMinBMinusR;
}
