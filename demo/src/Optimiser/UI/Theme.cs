using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Gapo.Optimiser.UI
{
    /// <summary>Design tokens: one place for colour, type, radius and spacing.</summary>
    public static class Theme
    {
        public static readonly Color Bg = Hex(0x0A0E1A), BgTop = Hex(0x0E1428);
        public static readonly Color Surface = Hex(0x121829), SurfaceHi = Hex(0x182036), SurfaceLo = Hex(0x0F1424);
        public static readonly Color Border = Hex(0x232C47), BorderHi = Hex(0x33406A);
        public static readonly Color Text = Hex(0xE8EDF8), TextDim = Hex(0x98A3BD), TextFaint = Hex(0x68728E);
        public static readonly Color Accent = Hex(0x22D3EE), Accent2 = Hex(0x8B5CF6);
        public static readonly Color Good = Hex(0x34D399), Warn = Hex(0xFBBF24), Bad = Hex(0xF87171), Info = Hex(0x60A5FA);

        public static Color Hex(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
        public static Color A(Color c, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c); }
        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        static string _family, _display;
        static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>();

        static void PickFamilies()
        {
            if (_family != null) return;
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { using (var fc = new InstalledFontCollection()) foreach (var f in fc.Families) installed.Add(f.Name); } catch { }
            _family = installed.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : installed.Contains("Segoe UI") ? "Segoe UI" : installed.Contains("DejaVu Sans") ? "DejaVu Sans" : FontFamily.GenericSansSerif.Name;
            _display = installed.Contains("Segoe UI Variable Display") ? "Segoe UI Variable Display" : installed.Contains("Segoe UI Semibold") ? "Segoe UI Semibold" : _family;
        }

        /// <summary>Font in device pixels (already DPI-scaled by the caller).</summary>
        public static Font Font(float px, bool bold = false, bool display = false)
        {
            PickFamilies();
            string fam = display ? _display : _family;
            bool useBold = bold && !(display && _display.EndsWith("Semibold"));
            string key = fam + "|" + px.ToString("0.0") + "|" + useBold;
            Font f;
            if (!Cache.TryGetValue(key, out f))
            {
                f = new Font(fam, px, useBold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                Cache[key] = f;
            }
            return f;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
