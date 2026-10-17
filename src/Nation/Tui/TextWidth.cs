using System.Globalization;
using System.Text;

namespace Nation.Tui;

/// <summary>Terminal column width of text (East Asian wide + emoji = 2, combining marks = 0).</summary>
public static class TextWidth
{
    public static int Of(string text)
    {
        int w = 0;
        foreach (var r in text.EnumerateRunes()) w += Of(r);
        return w;
    }

    public static int Of(Rune r)
    {
        int v = r.Value;
        if (v == 0) return 0;
        var cat = Rune.GetUnicodeCategory(r);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            return 0;
        if (v is 0xFE0F or 0x200D) return 0; // emoji presentation selector / ZWJ
        return IsWide(v) ? 2 : 1;
    }

    private static bool IsWide(int v) =>
        v is >= 0x1100 and <= 0x115F       // Hangul Jamo
        or >= 0x2E80 and <= 0x303E         // CJK radicals, punctuation
        or >= 0x3041 and <= 0x33FF         // Kana, CJK symbols
        or >= 0x3400 and <= 0x4DBF         // CJK ext A
        or >= 0x4E00 and <= 0x9FFF         // CJK unified
        or >= 0xA000 and <= 0xA4CF         // Yi
        or >= 0xAC00 and <= 0xD7A3         // Hangul syllables
        or >= 0xF900 and <= 0xFAFF         // CJK compatibility
        or >= 0xFE30 and <= 0xFE4F
        or >= 0xFF00 and <= 0xFF60         // Fullwidth forms
        or >= 0xFFE0 and <= 0xFFE6
        or >= 0x1F300 and <= 0x1F64F       // Emoji
        or >= 0x1F900 and <= 0x1F9FF
        or >= 0x20000 and <= 0x3FFFD;      // CJK ext B+
}
