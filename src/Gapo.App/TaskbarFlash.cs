using System.Runtime.InteropServices;

namespace Gapo.App;

internal static partial class TaskbarFlash
{
    private const uint FlashCaptionAndTray = 0x3;
    private const uint FlashUntilForeground = 0xC;

    public static bool UntilActivated(nint window)
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = window,
            Flags = FlashCaptionAndTray | FlashUntilForeground,
            Count = 0,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
        return window != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);
}
