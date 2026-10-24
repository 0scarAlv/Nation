using System.Runtime.InteropServices;

namespace Nation.Streaming;

/// <summary>
/// Reads text from the Windows clipboard. A session cookie is a few kilobytes of text;
/// typing or pasting that into a terminal text field is fragile, so Nation reads it
/// straight from the clipboard instead.
/// </summary>
public static partial class Clipboard
{
    private const uint UnicodeText = 13;

    public static string? ReadText()
    {
        // Another app may be holding the clipboard for a moment.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    IntPtr handle = GetClipboardData(UnicodeText);
                    if (handle == IntPtr.Zero) return null;
                    IntPtr data = GlobalLock(handle);
                    if (data == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(data); }
                    finally { GlobalUnlock(handle); }
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(20);
        }
        return null;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr handle);
}
