using System.Text;

namespace Nation.Tui;

/// <summary>
/// Cyberpunk effect helpers: deterministic noise and the glyphs of the rain visualizer.
/// Everything is keyed on (seed, tick) so effects are allocation-free and stable within a tick.
/// </summary>
public static class Fx
{
    public static readonly Rgb White = Rgb.Hex(0xF4F7FF);

    // Half-width katakana, hex and symbols for the rain visualizer: all one column wide.
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
}
