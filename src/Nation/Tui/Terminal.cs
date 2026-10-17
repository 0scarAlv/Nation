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
        EnableVirtualTerminal();
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

    /// <summary>A key press, or a tap of Shift on its own (pressed and released with no other key).</summary>
    public readonly record struct Input(ConsoleKeyInfo Key, bool ShiftTap);

    private readonly List<Input> _inputs = new();
    private readonly InputRecord[] _records = new InputRecord[32];
    private bool _shiftDown, _shiftClean;
    private long _shiftDownAt;

    /// <summary>
    /// Drains everything waiting in the console input buffer. Read raw (ReadConsoleInput)
    /// instead of Console.ReadKey, which silently discards modifier-only key events,
    /// so a lone Shift tap can open the command menu. The list is reused between calls.
    /// </summary>
    public List<Input> ReadInput()
    {
        _inputs.Clear();
        var handle = GetStdHandle(StdInputHandle);
        while (GetNumberOfConsoleInputEvents(handle, out uint pending) && pending > 0)
        {
            if (!ReadConsoleInputW(handle, _records, (uint)_records.Length, out uint read) || read == 0) break;
            for (int i = 0; i < read; i++)
            {
                if (_records[i].EventType != KeyEvent) continue; // resize/focus/mouse: the frame loop handles size
                Translate(_records[i].Key);
            }
        }
        return _inputs;
    }

    private void Translate(KeyEventRecord k)
    {
        const ushort VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkCapital = 0x14, VkLWin = 0x5B, VkRWin = 0x5C;
        bool down = k.KeyDown != 0;
        if (k.VirtualKeyCode == VkShift)
        {
            if (down && !_shiftDown) { _shiftDown = true; _shiftClean = true; _shiftDownAt = Environment.TickCount64; }
            else if (!down && _shiftDown)
            {
                _shiftDown = false;
                // A held Shift (or one used with another key) is not a tap.
                if (_shiftClean && Environment.TickCount64 - _shiftDownAt < 500) _inputs.Add(new(default, ShiftTap: true));
            }
            return;
        }
        if (!down) return;
        _shiftClean = false;
        if (k.VirtualKeyCode is VkControl or VkMenu or VkCapital or VkLWin or VkRWin) return;

        uint state = k.ControlKeyState;
        bool shift = (state & 0x10) != 0, alt = (state & 0x3) != 0, ctrl = (state & 0xC) != 0;
        // AltGr arrives as Ctrl+Alt with a printable character (e.g. @ on Spanish layouts): treat it as that character.
        if (alt && ctrl && k.UnicodeChar != 0) alt = ctrl = false;
        var info = new ConsoleKeyInfo(k.UnicodeChar, (ConsoleKey)k.VirtualKeyCode, shift, alt, ctrl);
        for (int r = 0; r < Math.Max((ushort)1, k.RepeatCount); r++) _inputs.Add(new(info, ShiftTap: false));
    }

    private const int StdInputHandle = -10, StdOutputHandle = -11;
    private const ushort KeyEvent = 0x0001;

    /// <summary>
    /// Windows Terminal interprets ANSI sequences out of the box, but the classic console
    /// (conhost) only does once asked; without this it prints the escape codes as text.
    /// </summary>
    private static void EnableVirtualTerminal()
    {
        const uint ProcessedOutput = 0x1, VirtualTerminalProcessing = 0x4;
        var handle = GetStdHandle(StdOutputHandle);
        if (GetConsoleMode(handle, out uint mode))
            SetConsoleMode(handle, mode | ProcessedOutput | VirtualTerminalProcessing);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyEventRecord Key;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KeyEventRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNumberOfConsoleInputEvents(IntPtr handle, out uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadConsoleInputW(IntPtr handle, [Out] InputRecord[] buffer, uint length, out uint read);

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
