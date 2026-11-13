using System;
using System.Runtime.InteropServices;

namespace Gapo.Sandbox
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys;
            public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        public static long AvailablePhysicalMb()
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref status) ? (long)(status.ullAvailPhys / (1024 * 1024)) : 0;
        }

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);

        public static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void UseDarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

        [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);


        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize; public int biWidth; public int biHeight; public short biPlanes; public short biBitCount;
            public int biCompression; public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
            public int biClrUsed; public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern int FillRect(IntPtr hdc, ref RECT rc, IntPtr brush);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr hdc, string text, int len, ref RECT rc, uint format);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int colorref);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc, int wSrc, int hSrc, IntPtr bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern int SetTextColor(IntPtr hdc, int colorref);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool TextOutW(IntPtr hdc, int x, int y, string text, int len);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
            uint strikeout, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

        public const uint SRCCOPY = 0x00CC0020;
        public const int COLORONCOLOR = 3;
        public const int TRANSPARENT = 1;
        public const uint DT_LEFT = 0, DT_CENTER = 1, DT_WORDBREAK = 0x10, DT_NOPREFIX = 0x800, DT_SINGLELINE = 0x20, DT_VCENTER = 4;

        public static int Rgb(int r, int g, int b) { return r | (g << 8) | (b << 16); }
    }
}
