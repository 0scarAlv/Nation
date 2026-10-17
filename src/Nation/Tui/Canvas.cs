using System.Runtime.InteropServices;
using System.Text;

namespace Nation.Tui;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(uint v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        static byte Mix(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t, MidpointRounding.AwayFromZero);
        return new(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t));
    }

    /// <summary>Packed 0x01RRGGBB; 0 means "terminal default".</summary>
    internal uint Packed => 0x0100_0000u | (uint)(R << 16 | G << 8 | B);
}

/// <summary>
/// One terminal cell, packed into 12 bytes with no padding so whole rows can be
/// compared as raw memory when diffing frames.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Cell
{
    /// <summary>Unicode scalar; 0 marks the right half of a wide glyph.</summary>
    public int Ch;
    /// <summary>Packed fg color (see Rgb.Packed), bit 25 = bold.</summary>
    public uint Fg;
    /// <summary>Packed bg color, 0 = default.</summary>
    public uint Bg;
}

/// <summary>
/// Off-screen cell grid. Each cell carries its own fg/bg so features like
/// half-block cover art and the spectrum can paint per-cell color.
/// </summary>
public sealed class Canvas
{
    public int Width { get; }
    public int Height { get; }
    private readonly Cell[] _cells;

    private const uint BoldBit = 0x0200_0000u;
    private const uint ColorBits = 0x01FF_FFFFu;
    private const int Space = ' ';

    public Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        _cells = new Cell[width * height];
        Clear();
    }

    public void Clear() => _cells.AsSpan().Fill(new Cell { Ch = Space });

    public void Set(int x, int y, char ch, Rgb? fg = null, Rgb? bg = null, bool bold = false) =>
        Set(x, y, new Rune(ch), fg, bg, bold);

    public void Set(int x, int y, Rune ch, Rgb? fg = null, Rgb? bg = null, bool bold = false)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        int i = y * Width + x;
        // Overwriting half of a wide glyph orphans the other half; blank it to keep columns aligned.
        if (ch.Value != 0)
        {
            if (_cells[i].Ch == 0 && x > 0) _cells[i - 1].Ch = Space;
            if (x + 1 < Width && _cells[i + 1].Ch == 0) _cells[i + 1].Ch = Space;
        }
        ref var c = ref _cells[i];
        c.Ch = ch.Value;
        c.Fg = (fg?.Packed ?? 0) | (bold ? BoldBit : 0);
        if (bg is { } b) c.Bg = b.Packed;
    }

    /// <summary>Writes text clipped to maxWidth columns (ellipsis when truncated). Returns columns used.</summary>
    public int Text(int x, int y, string text, Rgb? fg = null, Rgb? bg = null, bool bold = false, int maxWidth = int.MaxValue, bool ellipsis = true)
    {
        maxWidth = Math.Min(maxWidth, Width - x);
        if (maxWidth <= 0) return 0;
        bool truncate = ellipsis && TextWidth.Of(text) > maxWidth;
        int limit = truncate ? maxWidth - 1 : maxWidth;
        int col = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int w = TextWidth.Of(rune);
            if (w == 0) continue;
            if (col + w > limit) break;
            Set(x + col, y, rune, fg, bg, bold);
            if (w == 2) Set(x + col + 1, y, default(Rune), fg, bg, bold);
            col += w;
        }
        if (truncate && limit >= 0)
        {
            Set(x + col, y, '…', fg, bg, bold);
            col++;
        }
        return col;
    }

    /// <summary>Changes only the foreground of an already-drawn cell (keeps bold).</summary>
    public void Recolor(int x, int y, Rgb fg)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        ref var c = ref _cells[y * Width + x];
        c.Fg = fg.Packed | (c.Fg & BoldBit);
    }

    public void FillRow(int x, int y, int width, Rgb bg)
    {
        if (y < 0 || y >= Height) return;
        uint packed = bg.Packed;
        for (int i = Math.Max(0, x); i < x + width && i < Width; i++)
            _cells[y * Width + i].Bg = packed;
    }

    /// <summary>Rounded lazygit-style panel with the title embedded in the top border.</summary>
    public void Box(int x, int y, int w, int h, string title, Rgb border, Rgb titleColor)
    {
        if (w < 2 || h < 2) return;
        Set(x, y, '╭', border);
        Set(x + w - 1, y, '╮', border);
        Set(x, y + h - 1, '╰', border);
        Set(x + w - 1, y + h - 1, '╯', border);
        for (int i = 1; i < w - 1; i++)
        {
            Set(x + i, y, '─', border);
            Set(x + i, y + h - 1, '─', border);
        }
        for (int j = 1; j < h - 1; j++)
        {
            Set(x, y + j, '│', border);
            Set(x + w - 1, y + j, '│', border);
        }
        if (title.Length > 0 && w > 6)
        {
            Set(x + 2, y, ' ', titleColor);
            int used = Text(x + 3, y, title, titleColor, bold: true, maxWidth: w - 6);
            Set(x + 3 + used, y, ' ', titleColor);
        }
    }

    /// <summary>True when row y is identical in both canvases (same size assumed).</summary>
    public bool RowEquals(Canvas other, int y)
    {
        var a = MemoryMarshal.AsBytes(_cells.AsSpan(y * Width, Width));
        var b = MemoryMarshal.AsBytes(other._cells.AsSpan(y * Width, Width));
        return a.SequenceEqual(b);
    }

    /// <summary>Plain characters of one row, used for --snapshot debugging.</summary>
    public string RowText(int y)
    {
        var sb = new StringBuilder(Width);
        for (int x = 0; x < Width; x++)
        {
            int ch = _cells[y * Width + x].Ch;
            if (ch != 0) AppendRune(sb, ch);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Appends one row as ANSI (truecolor SGR) without intermediate strings.</summary>
    public void AppendRow(StringBuilder sb, int y)
    {
        uint fg = uint.MaxValue, bg = uint.MaxValue;
        for (int x = 0; x < Width; x++)
        {
            var c = _cells[y * Width + x];
            if (c.Fg != fg || c.Bg != bg)
            {
                sb.Append("\x1b[0");
                if ((c.Fg & BoldBit) != 0) sb.Append(";1");
                AppendColor(sb, "38", c.Fg & ColorBits);
                AppendColor(sb, "48", c.Bg);
                sb.Append('m');
                fg = c.Fg; bg = c.Bg;
            }
            // Right half of a wide glyph: the terminal already advanced two columns.
            if (c.Ch != 0) AppendRune(sb, c.Ch);
        }
        sb.Append("\x1b[0m");
    }

    private static void AppendColor(StringBuilder sb, string kind, uint packed)
    {
        if (packed == 0) return;
        sb.Append(';').Append(kind).Append(";2;")
          .Append((packed >> 16) & 0xFF).Append(';')
          .Append((packed >> 8) & 0xFF).Append(';')
          .Append(packed & 0xFF);
    }

    private static void AppendRune(StringBuilder sb, int scalar)
    {
        if (scalar < 0x10000) sb.Append((char)scalar);
        else
        {
            Span<char> pair = stackalloc char[2];
            new Rune(scalar).EncodeToUtf16(pair);
            sb.Append(pair);
        }
    }
}
