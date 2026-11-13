using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gapo.App.Overlay;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public sealed unsafe partial class OverlayWindow : IDisposable
{
    private const string ClassName = "GAPO.FpsOverlay";
    private const int Margin = 12;
    private const int Padding = 8;
    private const int Width = 230;
    private const int LineHeight = 20;
    private const byte Opacity = 225;
    private const uint Background = 0x00201C1C;
    private const uint Foreground = 0x00F5F3F2;
    private const uint Secondary = 0x00B5ADA8;
    private const int FontHeight = -15;

    private static readonly Dictionary<nint, OverlayWindow> Windows = [];
    private static bool classRegistered;

    private readonly nint owner;
    private readonly nint window;
    private readonly nint font;
    private readonly nint brush;
    private string[] lines = [];
    private bool visible;

    public OverlayWindow()
    {
        var instance = Native.GetModuleHandleW(null);
        if (!classRegistered)
        {
            fixed (char* name = ClassName)
            {
                var windowClass = new Native.WindowClass
                {
                    Size = (uint)sizeof(Native.WindowClass),
                    Procedure = &WindowProcedure,
                    Instance = instance,
                    ClassName = name,
                };
                Native.RegisterClassExW(&windowClass);
            }

            classRegistered = true;
        }

        const uint ExLayered = 0x00080000;
        const uint ExTransparent = 0x00000020;
        const uint ExTopmost = 0x00000008;
        const uint ExToolWindow = 0x00000080;
        const uint ExNoActivate = 0x08000000;
        const uint Popup = 0x80000000;
        fixed (char* name = ClassName)
        {
            owner = Native.CreateWindowExW(ExToolWindow | ExNoActivate, name, name, Popup, 0, 0, 0, 0, 0, 0, instance, null);
            window = Native.CreateWindowExW(ExLayered | ExTransparent | ExTopmost | ExToolWindow | ExNoActivate, name, name, Popup, 0, 0, Width, LineHeight * 2, owner, 0, instance, null);
        }

        const uint AlphaFlag = 0x2;
        Native.SetLayeredWindowAttributes(window, 0, Opacity, AlphaFlag);
        fixed (char* face = "Segoe UI")
        {
            font = Native.CreateFontW(FontHeight, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, face);
        }

        brush = Native.CreateSolidBrush(Background);
        Windows[window] = this;
    }

    public bool IsShowing => visible;

    public void Show(string[] text, Native.Rect gameWindow, OverlayCorner corner)
    {
        lines = text;
        var height = (Padding * 2) + (LineHeight * Math.Max(1, text.Length));
        var x = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? gameWindow.Left + Margin : gameWindow.Right - Width - Margin;
        var y = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? gameWindow.Top + Margin : gameWindow.Bottom - height - Margin;
        const uint NoActivate = 0x0010;
        const uint ShowWindow = 0x0040;
        var topmost = new nint(-1);
        Native.SetWindowPos(window, topmost, x, y, Width, height, NoActivate | ShowWindow);
        Native.InvalidateRect(window, null, true);
        visible = true;
    }

    public void Hide()
    {
        if (!visible)
        {
            return;
        }

        const int Hidden = 0;
        Native.ShowWindow(window, Hidden);
        visible = false;
    }

    public void Dispose()
    {
        Windows.Remove(window);
        Native.DestroyWindow(window);
        Native.DestroyWindow(owner);
        Native.DeleteObject(font);
        Native.DeleteObject(brush);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam)
    {
        const uint Paint = 0x000F;
        const uint HitTest = 0x0084;
        const uint Close = 0x0010;
        const nint Transparent = -1;
        if (message == HitTest)
        {
            return Transparent;
        }

        if (message == Close)
        {
            return 0;
        }

        if (message == Paint && Windows.TryGetValue(handle, out var overlay))
        {
            overlay.Paint();
            return 0;
        }

        return Native.DefWindowProcW(handle, message, wParam, lParam);
    }

    private void Paint()
    {
        var paint = default(Native.PaintStruct);
        var context = Native.BeginPaint(window, &paint);
        Native.GetClientRect(window, out var area);
        Native.FillRect(context, &area, brush);
        Native.SelectObject(context, font);
        const int TransparentBackground = 1;
        Native.SetBkMode(context, TransparentBackground);
        for (var index = 0; index < lines.Length; index++)
        {
            Native.SetTextColor(context, index == 0 ? Foreground : Secondary);
            var line = new Native.Rect { Left = Padding, Top = Padding + (index * LineHeight), Right = Width - Padding, Bottom = Padding + ((index + 1) * LineHeight) };
            const uint SingleLine = 0x20;
            const uint NoPrefix = 0x800;
            fixed (char* text = lines[index])
            {
                Native.DrawTextW(context, text, lines[index].Length, &line, SingleLine | NoPrefix);
            }
        }

        Native.EndPaint(window, &paint);
    }

    public static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowClass
        {
            public uint Size;
            public uint Style;
            public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> Procedure;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            public char* MenuName;
            public char* ClassName;
            public nint SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PaintStruct
        {
            public nint Context;
            public int Erase;
            public Rect Area;
            public int Restore;
            public int IncrementalUpdate;
            public fixed byte Reserved[32];
        }

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint GetModuleHandleW(string? name);

        [LibraryImport("user32.dll")]
        public static partial ushort RegisterClassExW(WindowClass* windowClass);

        [LibraryImport("user32.dll")]
        public static partial nint CreateWindowExW(uint extendedStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, void* parameter);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyWindow(nint window);

        [LibraryImport("user32.dll")]
        public static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ShowWindow(nint window, int command);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool InvalidateRect(nint window, Rect* area, [MarshalAs(UnmanagedType.Bool)] bool erase);

        [LibraryImport("user32.dll")]
        public static partial nint BeginPaint(nint window, PaintStruct* paint);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool EndPaint(nint window, PaintStruct* paint);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetClientRect(nint window, out Rect area);

        [LibraryImport("user32.dll")]
        public static partial int FillRect(nint context, Rect* area, nint brush);

        [LibraryImport("user32.dll")]
        public static partial int DrawTextW(nint context, char* text, int length, Rect* area, uint format);

        [LibraryImport("gdi32.dll")]
        public static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, char* face);

        [LibraryImport("gdi32.dll")]
        public static partial nint CreateSolidBrush(uint color);

        [LibraryImport("gdi32.dll")]
        public static partial nint SelectObject(nint context, nint gdiObject);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DeleteObject(nint gdiObject);

        [LibraryImport("gdi32.dll")]
        public static partial int SetBkMode(nint context, int mode);

        [LibraryImport("gdi32.dll")]
        public static partial uint SetTextColor(nint context, uint color);
    }
}
