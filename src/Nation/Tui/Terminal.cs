using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nation.Tui;

/// <summary>
/// Owns the real terminal: alternate screen, cursor, and diffed frame output.
/// Two canvases are reused (front = what's on screen, back = being drawn), so a
/// steady frame allocates nothing and only changed rows are serialized.
/// </summary>
public sealed class Terminal : IDisposable
{
    private readonly Stream _out = Console.OpenStandardOutput();
    private readonly StringBuilder _sb = new();
    private char[] _chars = new char[16 * 1024];
    private byte[] _bytes = new byte[48 * 1024];
    private Canvas? _front, _back;
    private bool _forceAll;

    /// <summary>Signaled while console input (keys, resize) is waiting to be read.</summary>
    public WaitHandle InputHandle { get; }

    public Terminal()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.TreatControlCAsInput = true;
        InputHandle = new ConsoleInputWaitHandle();
        Write("\x1b[?1049h\x1b[?25l\x1b[2J");
    }

    /// <summary>Returns the cleared back canvas, reallocating both on resize.</summary>
    public Canvas NewFrame()
    {
        int w = Math.Max(Console.WindowWidth, 20), h = Math.Max(Console.WindowHeight, 8);
        if (_back is null || _front is null || w != _back.Width || h != _back.Height)
        {
            _back = new Canvas(w, h);
            _front = new Canvas(w, h);
            _forceAll = true;
            Write("\x1b[2J");
        }
        else _back.Clear();
        return _back;
    }

    /// <summary>Writes only rows that differ from what's on screen, then swaps buffers.</summary>
    public void Present(Canvas canvas)
    {
        if (_front is null || !ReferenceEquals(canvas, _back)) return;
        _sb.Clear();
        for (int y = 0; y < canvas.Height; y++)
        {
            if (!_forceAll && canvas.RowEquals(_front, y)) continue;
            _sb.Append("\x1b[").Append(y + 1).Append(";1H");
            canvas.AppendRow(_sb, y);
        }
        _forceAll = false;
        (_front, _back) = (_back, _front);
        if (_sb.Length > 0) Flush();
    }

    private void Flush()
    {
        int n = _sb.Length;
        if (_chars.Length < n) _chars = new char[n * 2];
        _sb.CopyTo(0, _chars, 0, n);
        int max = Encoding.UTF8.GetMaxByteCount(n);
        if (_bytes.Length < max) _bytes = new byte[max];
        int len = Encoding.UTF8.GetBytes(_chars, 0, n, _bytes, 0);
        _out.Write(_bytes, 0, len);
        _out.Flush();
    }

    private void Write(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        _out.Write(bytes);
        _out.Flush();
    }

    public void Dispose() => Write("\x1b[0m\x1b[?25h\x1b[?1049l");

    /// <summary>The console input handle is a waitable kernel object: lets the loop sleep until a key arrives.</summary>
    private sealed class ConsoleInputWaitHandle : WaitHandle
    {
        private const int StdInputHandle = -10;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        public ConsoleInputWaitHandle() =>
            SafeWaitHandle = new SafeWaitHandle(GetStdHandle(StdInputHandle), ownsHandle: false);
    }
}
