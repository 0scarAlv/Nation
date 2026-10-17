using Nation.Tui;

namespace Nation.Art;

/// <summary>Readable accent for controls + deep tint for highlights, pulled from the cover.</summary>
public readonly record struct ArtPalette(Rgb Accent, Rgb Backdrop)
{
    public static readonly ArtPalette Default = new(Rgb.Hex(0xE8A33D), Rgb.Hex(0x2A2318));

    // Black-and-white covers have no hue worth amplifying.
    public static readonly ArtPalette Monochrome = new(Rgb.Hex(0xE4E4E7), Rgb.Hex(0x26262B));

    private const int HueBuckets = 36;
    private const float MinVividShare = 0.015f;

    /// <summary>
    /// Same algorithm as Shade's Android ArtPalette.kt: hue histogram weighted toward
    /// vivid pixels (saturation² × value), smoothed across neighbor buckets, then the
    /// winning bucket's mean color is re-lit for each role.
    /// </summary>
    public static ArtPalette Extract(byte[] bgra, int pixelCount)
    {
        var score = new float[HueBuckets];
        var red = new float[HueBuckets];
        var green = new float[HueBuckets];
        var blue = new float[HueBuckets];

        for (int p = 0; p < pixelCount; p++)
        {
            int o = p * 4;
            byte b = bgra[o], g = bgra[o + 1], r = bgra[o + 2], a = bgra[o + 3];
            if (a < 128) continue;
            var (h, s, v) = ToHsv(r, g, b);
            if (v < 0.18f || s < 0.22f) continue;

            float weight = s * s * v;
            int bucket = Math.Clamp((int)(h / 360f * HueBuckets), 0, HueBuckets - 1);
            score[bucket] += weight;
            red[bucket] += r * weight;
            green[bucket] += g * weight;
            blue[bucket] += b * weight;
        }

        int best = -1;
        float bestScore = 0;
        for (int i = 0; i < HueBuckets; i++)
        {
            float smoothed = score[i] + 0.5f * score[(i + HueBuckets - 1) % HueBuckets] + 0.5f * score[(i + 1) % HueBuckets];
            if (smoothed > bestScore) { bestScore = smoothed; best = i; }
        }
        if (best < 0 || score[best] < pixelCount * MinVividShare) return Monochrome;

        float total = score[best];
        var (hue, sat, _) = ToHsl(red[best] / total, green[best] / total, blue[best] / total);
        return new ArtPalette(
            FromHsl(hue, Math.Clamp(sat, 0.55f, 0.9f), 0.64f),
            FromHsl(hue, Math.Clamp(sat, 0.25f, 0.55f), 0.18f));
    }

    private static (float H, float S, float V) ToHsv(byte r8, byte g8, byte b8)
    {
        float r = r8 / 255f, g = g8 / 255f, b = b8 / 255f;
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        return (Hue(r, g, b, max, d), max == 0 ? 0 : d / max, max);
    }

    private static (float H, float S, float L) ToHsl(float r, float g, float b)
    {
        r /= 255f; g /= 255f; b /= 255f;
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        float l = (max + min) / 2;
        float s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return (Hue(r, g, b, max, d), s, l);
    }

    private static float Hue(float r, float g, float b, float max, float d)
    {
        if (d == 0) return 0;
        float h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static Rgb FromHsl(float h, float s, float l)
    {
        float c = (1 - Math.Abs(2 * l - 1)) * s;
        float x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        float m = l - c / 2;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0f),
            < 2 => (x, c, 0f),
            < 3 => (0f, c, x),
            < 4 => (0f, x, c),
            < 5 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return new Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
