using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Gapo.Optimiser.UI
{
    public enum Glyph { Cpu, Gpu, Ram, Disk, Display, Power, Windows, Check, Revert, Cross, Info, Warn, Bolt, ChevronDown, ChevronUp, Play, Stop, Doc, Spark, Pulse }

    /// <summary>Stateless drawing helpers used by the immediate-mode UI.</summary>
    public static class Gfx
    {
        public static void Card(Graphics g, RectangleF r, float s, bool raised = false)
        {
            using (var p = Theme.Round(r, 16 * s))
            {
                using (var b = new LinearGradientBrush(new PointF(r.X, r.Y), new PointF(r.X, r.Bottom + 1), raised ? Theme.SurfaceHi : Theme.Surface, raised ? Theme.Surface : Theme.SurfaceLo))
                    g.FillPath(b, p);
                using (var pen = new Pen(Theme.Border, Math.Max(1f, s))) g.DrawPath(pen, p);
            }
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            using (var p = Theme.Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float radius, Color c, float w)
        {
            using (var p = Theme.Round(r, radius)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
        }

        public static void Gradient(Graphics g, RectangleF r, float radius, Color a, Color b, float angle = 0)
        {
            if (r.Width < 1 || r.Height < 1) return;
            using (var p = Theme.Round(r, radius)) using (var br = new LinearGradientBrush(r, a, b, angle)) g.FillPath(br, p);
        }

        public static SizeF Measure(Graphics g, string text, Font f, float maxWidth = 0)
        {
            using (var sf = Format(false))
                return maxWidth > 0 ? g.MeasureString(text, f, (int)Math.Ceiling(maxWidth), sf) : g.MeasureString(text, f, PointF.Empty, sf);
        }

        static StringFormat Format(bool clip)
        {
            var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            if (!clip) sf.FormatFlags |= StringFormatFlags.NoClip;
            return sf;
        }

        public static void Text(Graphics g, string text, Font f, Color c, float x, float y)
        {
            using (var b = new SolidBrush(c)) using (var sf = Format(false)) g.DrawString(text, f, b, x, y, sf);
        }

        public static void TextRight(Graphics g, string text, Font f, Color c, float right, float y)
        {
            var sz = Measure(g, text, f);
            Text(g, text, f, c, right - sz.Width, y);
        }

        public static void TextCenter(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            using (var b = new SolidBrush(c)) using (var sf = Format(false))
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, f, b, r, sf);
            }
        }

        /// <summary>Wrapped text; returns the height used.</summary>
        public static float Wrap(Graphics g, string text, Font f, Color c, float x, float y, float width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            using (var b = new SolidBrush(c)) using (var sf = Format(false))
            {
                var size = g.MeasureString(text, f, (int)width, sf);
                g.DrawString(text, f, b, new RectangleF(x, y, width, size.Height + 4), sf);
                return size.Height;
            }
        }

        public static float WrapHeight(Graphics g, string text, Font f, float width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            using (var sf = Format(false)) return g.MeasureString(text, f, (int)width, sf).Height;
        }

        public static void GradientText(Graphics g, string text, Font f, float x, float y, Color a, Color b)
        {
            using (var path = new GraphicsPath())
            {
                path.AddString(text, f.FontFamily, (int)f.Style, f.Size, new PointF(x, y), StringFormat.GenericTypographic);
                var bounds = path.GetBounds();
                if (bounds.Width < 1) return;
                using (var br = new LinearGradientBrush(new RectangleF(bounds.X - 1, bounds.Y - 1, bounds.Width + 2, bounds.Height + 2), a, b, 20f))
                    g.FillPath(br, path);
            }
        }

        public static SizeF MeasurePath(string text, Font f)
        {
            using (var path = new GraphicsPath())
            {
                path.AddString(text, f.FontFamily, (int)f.Style, f.Size, PointF.Empty, StringFormat.GenericTypographic);
                var b = path.GetBounds();
                return new SizeF(b.Right, b.Bottom);
            }
        }

        public static void Pill(Graphics g, RectangleF r, Color c, string text, Font f, bool filled = false)
        {
            Fill(g, r, r.Height / 2, Theme.A(c, filled ? 255 : 38));
            if (!filled) Stroke(g, r, r.Height / 2, Theme.A(c, 90), 1f);
            TextCenter(g, text, f, filled ? Theme.Bg : c, r);
        }

        public static float PillWidth(Graphics g, string text, Font f, float s) { return Measure(g, text, f).Width + 22 * s; }

        public static void Dot(Graphics g, float cx, float cy, float r, Color c)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        }

        /// <summary>Visual cost as five dots (0..10 → 0..5 filled).</summary>
        public static void CostDots(Graphics g, float x, float cy, float s, int cost)
        {
            int filled = (int)Math.Round(Math.Max(0, cost) / 2.0);
            for (int i = 0; i < 5; i++)
            {
                Color c = i < filled ? (cost >= 5 ? Theme.Bad : cost >= 3 ? Theme.Warn : Theme.Accent) : Theme.A(Theme.TextFaint, 90);
                Dot(g, x + i * 9 * s, cy, 3 * s, c);
            }
        }

        public static void Ring(Graphics g, RectangleF r, float s, float fraction, float thickness, bool spin, float phase)
        {
            using (var bg = new Pen(Theme.A(Theme.BorderHi, 150), thickness)) g.DrawEllipse(bg, r);
            if (fraction <= 0.001f && !spin) return;
            using (var br = new LinearGradientBrush(r, Theme.Accent, Theme.Accent2, 45f))
            using (var pen = new Pen(br, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float sweep = Math.Max(2f, 360f * Math.Min(1f, fraction));
                g.DrawArc(pen, r, -90f + (spin ? phase : 0f), sweep);
            }
        }

        /// <summary>Area chart of frame times (ms), newest on the right, with 60/30 FPS guide lines.</summary>
        public static void FrameChart(Graphics g, RectangleF r, float s, IList<float> values, float maxMs, Color line, IList<float> overlay = null, Color? overlayColor = null, IList<float> markers = null)
        {
            Fill(g, r, 12 * s, Theme.A(Theme.Bg, 160));
            var guideFont = Theme.Font(10.5f * s);
            foreach (var guide in new[] { 16.7f, 33.3f })
            {
                if (guide > maxMs) continue;
                float y = r.Bottom - 8 * s - (r.Height - 16 * s) * guide / maxMs;
                using (var pen = new Pen(Theme.A(Theme.TextFaint, 70), 1f) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, r.X + 8 * s, y, r.Right - 8 * s, y);
                Text(g, guide < 20 ? "60 FPS" : "30 FPS", guideFont, Theme.A(Theme.TextFaint, 200), r.X + 10 * s, y - 15 * s);
            }
            if (markers != null)
                foreach (var m in markers)
                {
                    float x = r.X + 8 * s + (r.Width - 16 * s) * m;
                    using (var pen = new Pen(Theme.A(Theme.Accent2, 120), 1.5f * s) { DashStyle = DashStyle.Dot }) g.DrawLine(pen, x, r.Y + 6 * s, x, r.Bottom - 6 * s);
                }
            if (overlay != null && overlay.Count > 1) Series(g, r, s, overlay, maxMs, overlayColor ?? Theme.TextFaint, false);
            if (values != null && values.Count > 1) Series(g, r, s, values, maxMs, line, true);
        }

        static void Series(Graphics g, RectangleF r, float s, IList<float> values, float maxMs, Color color, bool fill)
        {
            var inner = new RectangleF(r.X + 8 * s, r.Y + 8 * s, r.Width - 16 * s, r.Height - 16 * s);
            var pts = new PointF[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                float v = Math.Min(maxMs, Math.Max(0, values[i]));
                pts[i] = new PointF(inner.X + inner.Width * i / (values.Count - 1), inner.Bottom - inner.Height * v / maxMs);
            }
            if (fill)
            {
                using (var path = new GraphicsPath())
                {
                    path.AddLines(pts);
                    path.AddLine(pts[pts.Length - 1], new PointF(inner.Right, inner.Bottom));
                    path.AddLine(new PointF(inner.Right, inner.Bottom), new PointF(inner.X, inner.Bottom));
                    path.CloseFigure();
                    using (var br = new LinearGradientBrush(new RectangleF(inner.X, inner.Y - 1, inner.Width, inner.Height + 2), Theme.A(color, 95), Theme.A(color, 0), 90f))
                        g.FillPath(br, path);
                }
            }
            using (var pen = new Pen(color, (fill ? 2f : 1.5f) * s) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);
        }

        /// <summary>Horizontal stacked bar of frame stages, with an optional budget marker.</summary>
        public static void StageBar(Graphics g, RectangleF r, float s, IList<Engine.StageShare> stages, double totalMs, double budgetMs, double scaleMs)
        {
            Fill(g, r, r.Height / 2, Theme.A(Theme.Bg, 180));
            float x = r.X;
            var state = g.Save();
            using (var clip = Theme.Round(r, r.Height / 2)) g.SetClip(clip);
            foreach (var st in stages)
            {
                if (st.Ms <= 0) continue;
                float w = (float)(r.Width * st.Ms / scaleMs);
                using (var b = new SolidBrush(Theme.Hex(st.Color))) g.FillRectangle(b, x, r.Y, Math.Max(1, w - 1.5f * s), r.Height);
                x += w;
            }
            g.Restore(state);
            if (budgetMs > 0 && budgetMs < scaleMs)
            {
                float bx = r.X + (float)(r.Width * budgetMs / scaleMs);
                using (var pen = new Pen(Theme.Text, 2f * s)) g.DrawLine(pen, bx, r.Y - 5 * s, bx, r.Bottom + 5 * s);
            }
        }

        // ================================================================= icons

        public static void DrawIcon(Graphics g, Glyph icon, RectangleF r, Color c, float s)
        {
            float w = Math.Max(1.4f, 1.7f * s);
            using (var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var br = new SolidBrush(c))
            {
                float x = r.X, y = r.Y, W = r.Width, H = r.Height;
                Func<float, float, PointF> P = (px, py) => new PointF(x + W * px, y + H * py);
                switch (icon)
                {
                    case Glyph.Cpu:
                        using (var p = Theme.Round(new RectangleF(x + W * .22f, y + H * .22f, W * .56f, H * .56f), 3 * s)) g.DrawPath(pen, p);
                        g.DrawRectangle(pen, x + W * .38f, y + H * .38f, W * .24f, H * .24f);
                        foreach (var t in new[] { .35f, .5f, .65f })
                        {
                            g.DrawLine(pen, P(t, .08f), P(t, .2f)); g.DrawLine(pen, P(t, .8f), P(t, .92f));
                            g.DrawLine(pen, P(.08f, t), P(.2f, t)); g.DrawLine(pen, P(.8f, t), P(.92f, t));
                        }
                        break;
                    case Glyph.Gpu:
                        using (var p = Theme.Round(new RectangleF(x + W * .08f, y + H * .26f, W * .84f, H * .46f), 3 * s)) g.DrawPath(pen, p);
                        g.DrawEllipse(pen, x + W * .3f, y + H * .34f, W * .3f, H * .3f);
                        g.DrawLine(pen, P(.72f, .4f), P(.82f, .4f)); g.DrawLine(pen, P(.72f, .56f), P(.82f, .56f));
                        g.DrawLine(pen, P(.2f, .72f), P(.2f, .84f)); g.DrawLine(pen, P(.34f, .72f), P(.34f, .84f));
                        break;
                    case Glyph.Ram:
                        using (var p = Theme.Round(new RectangleF(x + W * .08f, y + H * .3f, W * .84f, H * .34f), 2 * s)) g.DrawPath(pen, p);
                        foreach (var t in new[] { .24f, .42f, .6f, .78f }) g.DrawRectangle(pen, x + W * (t - .06f), y + H * .38f, W * .1f, H * .16f);
                        foreach (var t in new[] { .2f, .35f, .65f, .8f }) g.DrawLine(pen, P(t, .64f), P(t, .76f));
                        break;
                    case Glyph.Disk:
                        using (var p = Theme.Round(new RectangleF(x + W * .12f, y + H * .22f, W * .76f, H * .56f), 4 * s)) g.DrawPath(pen, p);
                        g.DrawLine(pen, P(.12f, .56f), P(.88f, .56f));
                        g.FillEllipse(br, x + W * .7f, y + H * .63f, W * .08f, H * .08f);
                        break;
                    case Glyph.Display:
                        using (var p = Theme.Round(new RectangleF(x + W * .08f, y + H * .16f, W * .84f, H * .54f), 3 * s)) g.DrawPath(pen, p);
                        g.DrawLine(pen, P(.5f, .7f), P(.5f, .84f)); g.DrawLine(pen, P(.32f, .86f), P(.68f, .86f));
                        break;
                    case Glyph.Power:
                    case Glyph.Bolt:
                        g.FillPolygon(br, new[] { P(.56f, .06f), P(.2f, .56f), P(.46f, .56f), P(.4f, .94f), P(.8f, .4f), P(.54f, .4f) });
                        break;
                    case Glyph.Windows:
                        float q = W * .36f, gap = W * .06f, ox = x + W * .13f, oy = y + H * .13f;
                        g.FillRectangle(br, ox, oy, q, q); g.FillRectangle(br, ox + q + gap, oy, q, q);
                        g.FillRectangle(br, ox, oy + q + gap, q, q); g.FillRectangle(br, ox + q + gap, oy + q + gap, q, q);
                        break;
                    case Glyph.Check:
                        g.DrawLines(pen, new[] { P(.2f, .52f), P(.42f, .72f), P(.8f, .3f) });
                        break;
                    case Glyph.Revert:
                        g.DrawArc(pen, x + W * .2f, y + H * .22f, W * .6f, H * .6f, 200, 250);
                        g.DrawLines(pen, new[] { P(.14f, .3f), P(.22f, .5f), P(.42f, .44f) });
                        break;
                    case Glyph.Cross:
                        g.DrawLine(pen, P(.28f, .28f), P(.72f, .72f)); g.DrawLine(pen, P(.72f, .28f), P(.28f, .72f));
                        break;
                    case Glyph.Info:
                        g.DrawEllipse(pen, x + W * .1f, y + H * .1f, W * .8f, H * .8f);
                        g.DrawLine(pen, P(.5f, .46f), P(.5f, .7f));
                        g.FillEllipse(br, x + W * .45f, y + H * .27f, W * .1f, H * .1f);
                        break;
                    case Glyph.Warn:
                        g.DrawPolygon(pen, new[] { P(.5f, .12f), P(.9f, .84f), P(.1f, .84f) });
                        g.DrawLine(pen, P(.5f, .38f), P(.5f, .6f));
                        g.FillEllipse(br, x + W * .46f, y + H * .67f, W * .08f, H * .08f);
                        break;
                    case Glyph.ChevronDown: g.DrawLines(pen, new[] { P(.25f, .4f), P(.5f, .64f), P(.75f, .4f) }); break;
                    case Glyph.ChevronUp: g.DrawLines(pen, new[] { P(.25f, .62f), P(.5f, .38f), P(.75f, .62f) }); break;
                    case Glyph.Play: g.FillPolygon(br, new[] { P(.3f, .2f), P(.8f, .5f), P(.3f, .8f) }); break;
                    case Glyph.Stop: g.FillRectangle(br, x + W * .28f, y + H * .28f, W * .44f, H * .44f); break;
                    case Glyph.Doc:
                        g.DrawPolygon(pen, new[] { P(.24f, .1f), P(.62f, .1f), P(.78f, .28f), P(.78f, .9f), P(.24f, .9f) });
                        g.DrawLine(pen, P(.36f, .48f), P(.66f, .48f)); g.DrawLine(pen, P(.36f, .64f), P(.66f, .64f));
                        break;
                    case Glyph.Spark:
                        g.FillPolygon(br, new[] { P(.5f, .06f), P(.6f, .4f), P(.94f, .5f), P(.6f, .6f), P(.5f, .94f), P(.4f, .6f), P(.06f, .5f), P(.4f, .4f) });
                        break;
                    case Glyph.Pulse:
                        g.DrawLines(pen, new[] { P(.06f, .56f), P(.3f, .56f), P(.42f, .24f), P(.56f, .8f), P(.66f, .46f), P(.94f, .46f) });
                        break;
                }
            }
        }

        public static void Logo(Graphics g, RectangleF r, float s)
        {
            using (var p = Theme.Round(r, r.Width * 0.28f))
            {
                using (var b = new SolidBrush(Theme.Hex(0x0B1020))) g.FillPath(b, p);
                using (var br = new LinearGradientBrush(r, Theme.Accent, Theme.Accent2, 45f)) using (var pen = new Pen(br, Math.Max(1.5f, 2f * s))) g.DrawPath(pen, p);
            }
            using (var br = new LinearGradientBrush(r, Theme.Accent, Theme.Accent2, 0f))
            using (var pen = new Pen(br, Math.Max(2f, 2.6f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float x = r.X, y = r.Y, W = r.Width, H = r.Height;
                g.DrawLines(pen, new[] { new PointF(x + W * .2f, y + H * .58f), new PointF(x + W * .36f, y + H * .58f), new PointF(x + W * .44f, y + H * .36f), new PointF(x + W * .55f, y + H * .72f), new PointF(x + W * .64f, y + H * .48f), new PointF(x + W * .8f, y + H * .48f) });
            }
        }
    }
}
