using System.Text;

namespace Nation.Tui;

/// <summary>
/// Cyberpunk effect helpers: deterministic noise, scrambled glyphs and neon colors.
/// Everything is keyed on (seed, tick) so effects are allocation-free and stable within a tick.
/// </summary>
public static class Fx
{
    public static readonly Rgb White = Rgb.Hex(0xF4F7FF);
    public static readonly Rgb Cyan = Rgb.Hex(0x00F0FF);
    private static readonly Rgb FallbackNeon = Rgb.Hex(0xFF2BD6);

    // Half-width katakana, hex and symbols: all one column wide, so scrambled text keeps its layout.
    private const string NoiseGlyphs = "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789ABCDEF#$%&*+=<>/\\|";

    public static uint Hash(int a, int b)
    {
        uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u;
        h ^= h >> 15; h *= 0x2C1B3C6Du;
        h ^= h >> 12; h *= 0x297A2D39u;
        return h ^ (h >> 15);
    }

    /// <summary>Uniform value in [0, 1).</summary>
    public static double Noise(int a, int b) => (Hash(a, b) & 0xFFFFFF) / (double)0x1000000;

    public static Rune Glyph(int a, int b) => new(NoiseGlyphs[(int)(Hash(a, b) % (uint)NoiseGlyphs.Length)]);

    /// <summary>Saturated counterpart of the cover accent (hue rotated toward magenta/cyan), for two-tone neon.</summary>
    public static Rgb Neon(Rgb accent)
    {
        var (h, s, v) = ToHsv(accent);
        if (s < 0.15) return FallbackNeon; // grey covers have no hue to rotate
        return FromHsv((h + 150) % 360, Math.Max(s, 0.75), Math.Max(v, 0.95));
    }

    private static (double H, double S, double V) ToHsv(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * ((g - b) / d % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, max == 0 ? 0 : d / max, max);
    }

    private static Rgb FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x),
            3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        static byte B(double f) => (byte)Math.Round(Math.Clamp(f, 0, 1) * 255);
        return new(B(r + m), B(g + m), B(b + m));
    }
}
