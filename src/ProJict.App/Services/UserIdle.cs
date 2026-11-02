using System.Runtime.InteropServices;

namespace ProJict.App.Services;

/// <summary>How long nothing was typed or clicked on this computer (in any app), for showing you away.</summary>
public static class UserIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    public static TimeSpan Time
    {
        get
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            // Both count milliseconds since Windows started, in 32 bits that wrap after 49 days.
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
        }
    }
}
