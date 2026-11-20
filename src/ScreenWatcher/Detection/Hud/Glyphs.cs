using System.Text.Json;

namespace ScreenWatcher.Detection.Hud;

/// <summary>One digit font: mean ink images (0..1) of the digits 0-9 in a fixed cell, 1.0 layout.
/// Built from labelled HUD crops (see samples/README.md) and stored in glyphs.json.</summary>
public sealed class GlyphSet
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Raw ink per digit (row-major), 0..1.</summary>
    public float[][] Ink { get; } = new float[10][];
    /// <summary>Zero-mean, unit-length copies for normalised cross-correlation.</summary>
    public float[][] Normalised { get; } = new float[10][];

    public GlyphSet(int width, int height, IReadOnlyDictionary<string, string[]> rows)
    {
        Width = width;
        Height = height;
        for (int d = 0; d < 10; d++)
        {
            if (!rows.TryGetValue(d.ToString(), out var r) || r.Length != height || r.Any(s => s.Length != width))
                throw new InvalidDataException($"glyphs: digit {d} must be {height} rows of {width} characters");
            var ink = new float[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    ink[y * width + x] = Level(r[y][x]);
            Ink[d] = ink;
            float mean = ink.Average();
            var n = ink.Select(v => v - mean).ToArray();
            float len = MathF.Sqrt(n.Sum(v => v * v));
            if (len <= 0) throw new InvalidDataException($"glyphs: digit {d} is empty");
            for (int i = 0; i < n.Length; i++) n[i] /= len;
            Normalised[d] = n;
        }
    }

    /// <summary>'.' = 0, '1'..'9' = 0.1..0.9, '#' = 1.</summary>
    public static float Level(char c) => c == '#' ? 1f : c is >= '1' and <= '9' ? (c - '0') / 10f : 0f;
    public static char Symbol(float v) => v >= 0.95f ? '#' : v < 0.05f ? '.' : (char)('0' + Math.Clamp((int)MathF.Round(v * 10), 1, 9));

    public const string FileName = "glyphs.json";

    sealed record FontJson(int Width, int Height, Dictionary<string, string[]> Glyphs);

    public static IReadOnlyDictionary<string, GlyphSet> LoadAll(string path) => ParseAll(File.ReadAllText(path), path);

    public static IReadOnlyDictionary<string, GlyphSet> ParseAll(string json, string source = FileName)
    {
        var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var fonts = JsonSerializer.Deserialize<Dictionary<string, FontJson>>(json, opt)
            ?? throw new InvalidDataException($"Empty glyph file: {source}");
        return fonts.ToDictionary(f => f.Key, f => new GlyphSet(f.Value.Width, f.Value.Height, f.Value.Glyphs), StringComparer.OrdinalIgnoreCase);
    }
}
