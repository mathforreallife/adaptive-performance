using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gapo.App.Overlay;

public static unsafe partial class GameWindowLocator
{
    private const int MinimumGameWidth = 320;
    private const int ExclusiveFullScreen = 3;

    [ThreadStatic]
    private static int searchedProcess;

    [ThreadStatic]
    private static nint bestWindow;

    [ThreadStatic]
    private static long bestArea;

    public static nint MainWindowOf(int processId)
    {
        searchedProcess = processId;
        bestWindow = 0;
        bestArea = 0;
        EnumWindows(&Consider, 0);
        return bestWindow;
    }

    public static bool IsInFront(nint window) => window != 0 && GetForegroundWindow() == window && !IsIconic(window);

    public static bool TryGetBounds(nint window, out OverlayWindow.Native.Rect bounds)
    {
        bounds = default;
        if (!GetClientRect(window, out var client) || client.Right <= 0 || client.Bottom <= 0)
        {
            return false;
        }

        var topLeft = new Point { X = 0, Y = 0 };
        if (!ClientToScreen(window, ref topLeft))
        {
            return false;
        }

        bounds = new OverlayWindow.Native.Rect { Left = topLeft.X, Top = topLeft.Y, Right = topLeft.X + client.Right, Bottom = topLeft.Y + client.Bottom };
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    public static bool AnExclusiveFullScreenGameIsRunning() =>
        SHQueryUserNotificationState(out var state) == 0 && state == ExclusiveFullScreen;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Consider(nint window, nint parameter)
    {
        GetWindowThreadProcessId(window, out var processId);
        if (processId == searchedProcess && IsWindowVisible(window) && GetWindowRect(window, out var rect))
        {
            var width = rect.Right - rect.Left;
            var area = (long)width * (rect.Bottom - rect.Top);
            if (width >= MinimumGameWidth && area > bestArea)
            {
                bestArea = area;
                bestWindow = window;
            }
        }

        return 1;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint parameter);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out OverlayWindow.Native.Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out OverlayWindow.Native.Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref Point point);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
