using System.Runtime.InteropServices;

namespace Gapo.EndToEnd;

internal static partial class OverlayProbe
{
    private const string OverlayClass = "GAPO.FpsOverlay";
    private const int Minimise = 6;

    public static bool OverlayIsVisible()
    {
        var found = false;
        var window = 0;
        do
        {
            window = (int)FindWindowExW(0, window, OverlayClass, OverlayClass);
            if (window != 0 && IsWindowVisible(window) && GetWindowRect(window, out var rect) && rect.Right - rect.Left > 1)
            {
                found = true;
            }
        }
        while (window != 0 && !found);

        return found;
    }

    public static bool MinimiseWindow(nint window)
    {
        ShowWindow(window, Minimise);
        Thread.Sleep(TimeSpan.FromMilliseconds(500));
        return IsIconic(window);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string className, string title);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);
}
