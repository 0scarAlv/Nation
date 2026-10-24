using System.Runtime.InteropServices;
using System.Text;

namespace Nation.Audio;

/// <summary>Minimal P/Invoke surface of libmpv's client API (client.h, API 2.x).</summary>
internal static unsafe partial class MpvNative
{
    private const string Lib = "libmpv-2";

    public enum EventId
    {
        None = 0,
        Shutdown = 1,
        EndFile = 7,
        FileLoaded = 8,
        PropertyChange = 22,
    }

    public enum Format
    {
        None = 0,
        String = 1,
        Flag = 3,
        Int64 = 4,
        Double = 5,
    }

    public enum EndFileReason
    {
        Eof = 0,
        Stop = 2,
        Quit = 3,
        Error = 4,
        Redirect = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public EventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventEndFile
    {
        public EndFileReason Reason;
        public int Error;
        public long PlaylistEntryId;
    }

    [LibraryImport(Lib, EntryPoint = "mpv_create")]
    public static partial IntPtr Create();

    [LibraryImport(Lib, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetOptionString(IntPtr ctx, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyString(IntPtr ctx, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetProperty(IntPtr ctx, string name, Format format, void* data);

    [LibraryImport(Lib, EntryPoint = "mpv_command")]
    public static partial int Command(IntPtr ctx, IntPtr* args);

    [LibraryImport(Lib, EntryPoint = "mpv_wait_event")]
    public static partial Event* WaitEvent(IntPtr ctx, double timeout);

    [LibraryImport(Lib, EntryPoint = "mpv_wakeup")]
    public static partial void Wakeup(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_free")]
    public static partial void Free(void* data);

    [LibraryImport(Lib, EntryPoint = "mpv_error_string")]
    public static partial IntPtr ErrorString(int error);

    /// <summary>Runs an mpv command given as UTF-8 strings (NULL-terminated argv).</summary>
    public static int Command(IntPtr ctx, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = Utf8Alloc(args[i]);
            fixed (IntPtr* p = ptrs)
                return Command(ctx, p);
        }
        finally
        {
            foreach (var ptr in ptrs)
                if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }
    }

    public static string Error(int code) =>
        Marshal.PtrToStringUTF8(ErrorString(code)) ?? code.ToString();

    private static IntPtr Utf8Alloc(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var mem = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, mem, bytes.Length);
        Marshal.WriteByte(mem, bytes.Length, 0);
        return mem;
    }
}
