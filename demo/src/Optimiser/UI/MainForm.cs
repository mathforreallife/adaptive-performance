using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Gapo.Optimiser.Engine;
using Gapo.Optimiser.Game;
using Gapo.Optimiser.Hardware;
using Gapo.Optimiser.Monitoring;
using Gapo.Shared;

namespace Gapo.Optimiser.UI
{
    /// <summary>
    /// The optimiser window. Everything is custom-drawn in one immediate-mode pass (no child controls),
    /// which keeps the look consistent and the cost predictable: the window repaints only on a slow
    /// timer, input, or a short animation, and drops to 2 Hz while a measurement window is open.
    /// </summary>
    public sealed class MainForm : Form
    {
        enum View { Home, Running, Results }

        sealed class Hit { public RectangleF R; public string Id; public Action Click; }

        sealed class Live
        {
            public double Fps, Low1, FrameMs, StuttersPerMin, Low01, StdDev, Sim, Render, Present;
            public List<float> Chart = new List<float>();
            public Diagnosis Diag;
            public int Frames;
        }

        // Services
        readonly GameLink _link = new GameLink();
        readonly SystemMonitor _monitor = new SystemMonitor();
        HardwareProfile _hw;
        OptimisationEngine _engine;
        string _scanStep = "Starting";

        // UI state
        View _view = View.Home;
        bool _details;
        int _tab;
        float _scroll, _scrollMax;
        RectangleF _scrollArea;
        Goal _goal;
        float S = 1f;
        readonly List<Hit> _hits = new List<Hit>();
        string _hover, _pressed;
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _resultsShownAt = -1, _lastDiagAt, _lastSettingsAt, _lastLiveAt;
        Live _live = new Live();
        EngineStatus _status = new EngineStatus();
        SandboxSettings _gameSettings;
        Bitmap _bg;
        string _toast; double _toastUntil;
        double _selfCpuSum; int _selfCpuN;

        // Autotest (CI)
        readonly string _autotestDir;
        int _autoStage;
        double _autoStageAt;

        const double SelfCpuBudgetPct = 1.5, SelfRamBudgetMB = 120;

        public MainForm(string autotestDir, Goal goal)
        {
            _autotestDir = autotestDir;
            _goal = goal;
            Text = "GAPO — Adaptive Performance";
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            var area = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Min((int)(1120 * S), (int)(area.Width * 0.95)), h = Math.Min((int)(748 * S), (int)(area.Height * 0.94));
            ClientSize = new Size(w, h);
            MinimumSize = new Size(Math.Min((int)(900 * S), area.Width), Math.Min((int)(620 * S), area.Height));
            if (h < 700 * S) S = Math.Max(0.8f, S * h / (700 * S));

            _link.ConnectionChanged += connected => { try { BeginInvoke((Action)(() => OnConnection(connected))); } catch { } };
            _timer.Tick += (s, e) => Tick();
            _timer.Interval = 250;
            _timer.Start();
            new Thread(ScanHardware) { IsBackground = true, Name = "GAPO scan", Priority = ThreadPriority.BelowNormal }.Start();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.UseDarkTitleBar(Handle); }

        void ScanHardware()
        {
            string exe = _link.ExePath;
            var hw = HardwareScanner.Scan(exe, step => { _scanStep = step; SafeInvalidate(); });
            GC.Collect();   // WMI leaves a lot of short-lived garbage behind; return it before any game is measured
            _hw = hw;
            try { BeginInvoke((Action)(() => { _engine = new OptimisationEngine(_link, _monitor, _hw); _engine.Changed += s => { _status = s; SafeInvalidate(); }; Invalidate(); })); } catch { }
        }

        void SafeInvalidate() { try { if (IsHandleCreated) BeginInvoke((Action)(() => Invalidate())); } catch { } }

        void OnConnection(bool connected)
        {
            _monitor.SetGame(connected ? _link.Pid : 0);
            if (connected) Toast("Connected to " + (_link.Title ?? "game"));
            else if (_engine != null && _engine.Running) _engine.Cancel();
            Invalidate();
        }

        void Toast(string t) { _toast = t; _toastUntil = _clock.Elapsed.TotalMilliseconds + 3500; }

        // ================================================================= timer: data refresh + pacing of repaints

        void Tick()
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            bool measuring = _status != null && _status.Measuring;
            bool countUp = _resultsShownAt >= 0 && now - _resultsShownAt < 1400;
            // Repaint budget: 1 Hz while a measurement window is open, 25 Hz only for the short result
            // count-up, 4 Hz while tests run, 2 Hz for the live dashboard, 1 Hz when idle or minimised.
            _timer.Interval = WindowState == FormWindowState.Minimized ? 1000 : measuring ? 1000 : countUp ? 40 : _view == View.Running ? 250 : _link.Connected ? 500 : 1000;

            if (_engine != null && _engine.Running && _view != View.Running) { _view = View.Running; _details = false; }
            if (_view == View.Running && _engine != null && !_engine.Running)
            {
                _view = _engine.Report != null && _engine.Report.Completed ? View.Results : View.Home;
                if (_view == View.Results) _resultsShownAt = now;
                else if (_engine.Report != null && _engine.Report.Error != null) Toast(_engine.Report.Error);
            }

            if (_link.Connected && now - _lastLiveAt >= (measuring ? 950 : 450)) { _lastLiveAt = now; RefreshLive(now); }
            if (_link.Connected && now - _lastSettingsAt > 2000) { _lastSettingsAt = now; _gameSettings = _link.ReadSettings(); }
            var m = _monitor.Latest;
            if (m != null && m.TimeMs > 0 && m.SelfCpuPct > 0) { _selfCpuSum += m.SelfCpuPct; _selfCpuN++; }
            if (_autotestDir != null) AutotestStep(now);
            if (WindowState != FormWindowState.Minimized) Invalidate();
        }

        void RefreshLive(double now)
        {
            var frames = _link.LastFrames(900);
            if (frames.Count < 2) return;
            double end = frames[frames.Count - 1].TimeMs;
            var recent = frames.Where(f => f.TimeMs >= end - 3000).ToList();
            var st = FrameStats.Compute(recent);
            var lng = FrameStats.Compute(frames.Where(f => f.TimeMs >= end - 20000).ToList());
            var live = new Live { Fps = st.AvgFps, Low1 = st.Low1Fps, Low01 = st.Low01Fps, FrameMs = st.MeanFrameMs, StdDev = st.StdDevFrameMs, StuttersPerMin = lng.StuttersPerMin, Sim = st.Sim, Render = st.Render, Present = st.Present, Frames = st.Count, Diag = _live.Diag };
            // Chart: last 8 s, bucketed; keep the worst frame per bucket so spikes stay visible.
            var span = frames.Where(f => f.TimeMs >= end - 8000).ToList();
            int buckets = 150;
            if (span.Count > 0)
            {
                double t0 = end - 8000;
                var vals = new float[buckets];
                var has = new bool[buckets];
                foreach (var f in span)
                {
                    int b = Math.Min(buckets - 1, (int)((f.TimeMs - t0) / 8000 * buckets));
                    if (b < 0) continue;
                    if (!has[b] || f.FrameMs > vals[b]) vals[b] = f.FrameMs;
                    has[b] = true;
                }
                float last = span[0].FrameMs;
                for (int i = 0; i < buckets; i++) { if (has[i]) last = vals[i]; else vals[i] = last; live.Chart.Add(vals[i]); }
            }
            if (_hw != null && now - _lastDiagAt > 1000 && _view != View.Running)
            {
                _lastDiagAt = now;
                var mon = MonitorSummary.From(_monitor.History(_monitor.Now - 3500));
                var settings = _gameSettings ?? _link.ReadSettings();
                if (settings != null) live.Diag = Diagnosis.Run(st, mon, _hw, settings, _hw.EffectiveRefreshHz, lng);
            }
            _live = live;
        }

        // ================================================================= input

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = HitAt(e.Location);
            string id = h == null ? null : h.Id;
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            if (id != _hover) { _hover = id; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != null) { _hover = null; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var h = HitAt(e.Location);
            _pressed = h == null ? null : h.Id;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var h = HitAt(e.Location);
            if (h != null && h.Id == _pressed && h.Click != null) h.Click();
            _pressed = null;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_scrollArea.Contains(e.Location) && _scrollMax > 0)
            {
                _scroll = Math.Max(0, Math.Min(_scrollMax, _scroll - e.Delta * 0.6f * S));
                Invalidate();
            }
        }

        Hit HitAt(Point p)
        {
            for (int i = _hits.Count - 1; i >= 0; i--) if (_hits[i].R.Contains(p)) return _hits[i];
            return null;
        }

        void AddHit(string id, RectangleF r, Action click)
        {
            if (_clipArea.HasValue)
            {
                var c = _clipArea.Value;
                r = RectangleF.Intersect(new RectangleF(r.X, r.Y - _clipOffset, r.Width, r.Height), c);
                if (r.Width <= 0 || r.Height <= 0) return;
            }
            _hits.Add(new Hit { Id = id, R = r, Click = click });
        }

        RectangleF? _clipArea;
        float _clipOffset;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_engine != null && _engine.Running) { _engine.Cancel(); }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _link.Dispose();
            _monitor.Dispose();
            base.OnFormClosed(e);
        }

        // ================================================================= paint

        protected override void OnPaint(PaintEventArgs e) { PaintAll(e.Graphics, ClientSize); }

        void PaintAll(Graphics g, Size size)
        {
            _hits.Clear();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DrawBackground(g, size);
            float pad = 22 * S;
            float headerH = 66 * S;
            DrawHeader(g, new RectangleF(pad, 0, size.Width - 2 * pad, headerH));
            var content = new RectangleF(pad, headerH, size.Width - 2 * pad, size.Height - headerH - pad);
            switch (_view)
            {
                case View.Home: DrawHome(g, content); break;
                case View.Running: DrawRunning(g, content); break;
                case View.Results: DrawResults(g, content); break;
            }
            if (_toast != null && _clock.Elapsed.TotalMilliseconds < _toastUntil)
            {
                var f = Theme.Font(13 * S);
                float w = Gfx.Measure(g, _toast, f).Width + 40 * S;
                var r = new RectangleF((size.Width - w) / 2, size.Height - 64 * S, w, 40 * S);
                Gfx.Fill(g, r, 20 * S, Theme.A(Theme.SurfaceHi, 250));
                Gfx.Stroke(g, r, 20 * S, Theme.A(Theme.Accent, 120), 1f);
                Gfx.TextCenter(g, _toast, f, Theme.Text, r);
            }
        }

        void DrawBackground(Graphics g, Size size)
        {
            if (_bg == null || _bg.Width != size.Width || _bg.Height != size.Height)
            {
                if (_bg != null) _bg.Dispose();
                _bg = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
                using (var bg = Graphics.FromImage(_bg))
                {
                    bg.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var br = new LinearGradientBrush(new Rectangle(0, 0, size.Width, size.Height + 1), Theme.BgTop, Theme.Bg, 90f)) bg.FillRectangle(br, 0, 0, size.Width, size.Height);
                    Glow(bg, new RectangleF(-size.Width * 0.15f, -size.Height * 0.55f, size.Width * 0.7f, size.Height * 1.0f), Theme.Accent, 34);
                    Glow(bg, new RectangleF(size.Width * 0.55f, -size.Height * 0.45f, size.Width * 0.65f, size.Height * 0.9f), Theme.Accent2, 30);
                }
            }
            g.DrawImageUnscaled(_bg, 0, 0);
        }

        static void Glow(Graphics g, RectangleF r, Color c, int alpha)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using (var br = new PathGradientBrush(path) { CenterColor = Theme.A(c, alpha), SurroundColors = new[] { Theme.A(c, 0) } })
                    g.FillPath(br, path);
            }
        }

        // ================================================================= header

        void DrawHeader(Graphics g, RectangleF r)
        {
            float logo = 34 * S;
            Gfx.Logo(g, new RectangleF(r.X, r.Y + (r.Height - logo) / 2, logo, logo), S);
            var fTitle = Theme.Font(19 * S, true, true);
            Gfx.Text(g, "GAPO", fTitle, Theme.Text, r.X + logo + 12 * S, r.Y + r.Height / 2 - 14 * S);
            float tw = Gfx.Measure(g, "GAPO", fTitle).Width;
            Gfx.Text(g, "Adaptive Performance", Theme.Font(12.5f * S), Theme.TextDim, r.X + logo + 18 * S + tw, r.Y + r.Height / 2 - 7 * S);

            var fp = Theme.Font(12 * S);
            float h = 30 * S, y = r.Y + (r.Height - h) / 2, x = r.Right;
            // Connection pill
            string conn; Color cc;
            if (_link.Connected)
            {
                conn = (_link.Title ?? "Game") + (_live.Fps > 0 ? " · " + _live.Fps.ToString("0") + " FPS" : "");
                cc = Theme.Good;
            }
            else { conn = "Waiting for a game"; cc = Theme.Warn; }
            float w = Gfx.PillWidth(g, conn, fp, S) + 14 * S;
            x -= w;
            var pr = new RectangleF(x, y, w, h);
            Gfx.Fill(g, pr, h / 2, Theme.A(Theme.Surface, 230));
            Gfx.Stroke(g, pr, h / 2, Theme.Border, 1f);
            Gfx.Dot(g, pr.X + 15 * S, pr.Y + h / 2, 4 * S, cc);
            Gfx.Text(g, conn, fp, Theme.Text, pr.X + 25 * S, pr.Y + (h - 15 * S) / 2);

            // Self-overhead pill: always visible, turns amber if GAPO ever exceeds its own budget.
            var m = _monitor.Latest;
            double cpu = _selfCpuN > 0 ? _selfCpuSum / _selfCpuN : (m != null ? m.SelfCpuPct : 0);
            double ram = m != null ? m.SelfPrivateBytes / 1048576.0 : 0;
            bool over = cpu > SelfCpuBudgetPct || ram > SelfRamBudgetMB;
            string ov = "GAPO uses " + cpu.ToString(cpu < 1 ? "0.0" : "0") + "% CPU · " + ram.ToString("0") + " MB" + (over ? " · over budget" : "");
            float ow = Gfx.PillWidth(g, ov, fp, S) + 10 * S;
            x -= ow + 10 * S;
            var or = new RectangleF(x, y, ow, h);
            Gfx.Fill(g, or, h / 2, Theme.A(Theme.Surface, 230));
            Gfx.Stroke(g, or, h / 2, over ? Theme.A(Theme.Warn, 160) : Theme.Border, 1f);
            Gfx.DrawIcon(g, Glyph.Pulse, new RectangleF(or.X + 9 * S, or.Y + 7 * S, 16 * S, 16 * S), over ? Theme.Warn : Theme.Accent, S);
            Gfx.Text(g, ov, fp, over ? Theme.Warn : Theme.TextDim, or.X + 30 * S, or.Y + (h - 15 * S) / 2);
        }

        // ================================================================= common widgets

        enum Btn { Primary, Secondary, Ghost }

        void Button(Graphics g, string id, RectangleF r, string text, Btn style, Action click, Glyph? icon = null, float fontPx = 14)
        {
            bool hover = _hover == id, down = _pressed == id && hover;
            float rad = Math.Min(12 * S, r.Height / 2);
            var f = Theme.Font(fontPx * S, true);
            if (style == Btn.Primary)
            {
                var a = hover ? Theme.Mix(Theme.Accent, Color.White, 0.12f) : Theme.Accent;
                var b = hover ? Theme.Mix(Theme.Accent2, Color.White, 0.12f) : Theme.Accent2;
                if (down) { a = Theme.Mix(a, Color.Black, 0.15f); b = Theme.Mix(b, Color.Black, 0.15f); }
                Gfx.Gradient(g, r, rad, a, b, 0);
            }
            else
            {
                Gfx.Fill(g, r, rad, style == Btn.Secondary ? (hover ? Theme.BorderHi : Theme.SurfaceHi) : (hover ? Theme.A(Theme.SurfaceHi, 255) : Theme.A(Theme.Surface, 120)));
                Gfx.Stroke(g, r, rad, hover ? Theme.A(Theme.Accent, 140) : Theme.Border, 1f);
            }
            Color tc = style == Btn.Primary ? Theme.Hex(0x06101F) : Theme.Text;
            var ts = Gfx.Measure(g, text, f);
            float iconW = icon.HasValue ? 20 * S : 0;
            float x = r.X + (r.Width - ts.Width - iconW) / 2;
            if (icon.HasValue) Gfx.DrawIcon(g, icon.Value, new RectangleF(x, r.Y + (r.Height - 16 * S) / 2, 16 * S, 16 * S), tc, S);
            Gfx.Text(g, text, f, tc, x + iconW, r.Y + (r.Height - ts.Height) / 2);
            AddHit(id, r, click);
        }

        void SectionLabel(Graphics g, string text, float x, float y)
        {
            Gfx.Text(g, text.ToUpperInvariant(), Theme.Font(11 * S, true), Theme.TextFaint, x, y);
        }

        void StatTile(Graphics g, RectangleF r, string label, string value, string unit, Color valueColor)
        {
            Gfx.Fill(g, r, 12 * S, Theme.A(Theme.Bg, 150));
            Gfx.Stroke(g, r, 12 * S, Theme.A(Theme.Border, 200), 1f);
            SectionLabel(g, label, r.X + 14 * S, r.Y + 12 * S);
            var fv = Theme.Font(27 * S, true, true);
            Gfx.Text(g, value, fv, valueColor, r.X + 13 * S, r.Y + 30 * S);
            float vw = Gfx.Measure(g, value, fv).Width;
            Gfx.Text(g, unit, Theme.Font(12 * S), Theme.TextDim, r.X + 17 * S + vw, r.Y + 44 * S);
        }

        static Color FpsColor(double fps) { return fps >= 55 ? Theme.Good : fps >= 38 ? Theme.Warn : Theme.Bad; }

        float Legend(Graphics g, IList<StageShare> stages, float x, float y, float maxW)
        {
            var f = Theme.Font(11.5f * S);
            float cx = x, cy = y;
            foreach (var st in stages)
            {
                if (st.Ms < 0.05) continue;
                string t = st.Name + " " + KnowledgeBase.F(st.Ms) + " ms";
                float w = Gfx.Measure(g, t, f).Width + 26 * S;
                if (cx + w > x + maxW) { cx = x; cy += 20 * S; }
                Gfx.Dot(g, cx + 5 * S, cy + 8 * S, 4 * S, Theme.Hex(st.Color));
                Gfx.Text(g, t, f, Theme.TextDim, cx + 14 * S, cy);
                cx += w;
            }
            return cy - y + 20 * S;
        }

        float DrawDiagnosisBlock(Graphics g, Diagnosis d, float x, float y, float w, bool compact)
        {
            float y0 = y;
            if (d == null)
            {
                Gfx.Text(g, "Analysing frames…", Theme.Font(15 * S, true), Theme.TextDim, x, y);
                return 24 * S;
            }
            Color tc = d.Primary == Bottleneck.TargetMet ? Theme.Good : d.Primary == Bottleneck.Warmup ? Theme.Warn : Theme.Text;
            Gfx.Text(g, d.Title, Theme.Font(16 * S, true), tc, x, y);
            y += 28 * S;
            double scale = Math.Max(d.WorkMs, d.BudgetMs) * 1.12;
            Gfx.StageBar(g, new RectangleF(x, y, w, 12 * S), S, d.Stages, d.WorkMs, d.BudgetMs, scale);
            y += 22 * S;
            y += Legend(g, d.Stages, x, y, w) + 4 * S;
            y += Gfx.Wrap(g, d.Summary, Theme.Font(12.5f * S), Theme.TextDim, x, y, w) + 4 * S;
            return y - y0;
        }

        // ================================================================= home

        void DrawHome(Graphics g, RectangleF c)
        {
            float gap = 18 * S;
            var hero = new RectangleF(c.X, c.Y, c.Width * 0.635f, c.Height);
            var side = new RectangleF(hero.Right + gap, c.Y, c.Right - hero.Right - gap, c.Height);
            Gfx.Card(g, hero, S, true);
            DrawPcCard(g, side);
            float p = 22 * S;
            var inner = new RectangleF(hero.X + p, hero.Y + p, hero.Width - 2 * p, hero.Height - 2 * p);

            if (!_link.Connected)
            {
                DrawNoGame(g, inner);
                return;
            }

            // Title row
            Gfx.Text(g, _link.Title ?? "Game", Theme.Font(20 * S, true, true), Theme.Text, inner.X, inner.Y - 2 * S);
            string sub = KnowledgeBase.EngineName;
            Gfx.Text(g, sub, Theme.Font(12.5f * S), Theme.TextDim, inner.X, inner.Y + 26 * S);
            if (_gameSettings != null)
            {
                bool def = _gameSettings.IsUnoptimisedDefaults();
                bool gapo = _gameSettings.ModifiedBy.IndexOf("GAPO", StringComparison.OrdinalIgnoreCase) >= 0;
                string chip = def ? "Unoptimised defaults" : gapo ? "Optimised by GAPO" : "Custom settings";
                Color cc = def ? Theme.Bad : gapo ? Theme.Good : Theme.Warn;
                var f = Theme.Font(11.5f * S, true);
                float w = Gfx.PillWidth(g, chip, f, S);
                Gfx.Pill(g, new RectangleF(inner.Right - w, inner.Y, w, 26 * S), cc, chip, f);
            }

            // Stat tiles
            float ty = inner.Y + 56 * S, th = 78 * S, tg = 12 * S, tw = (inner.Width - 3 * tg) / 4;
            StatTile(g, new RectangleF(inner.X, ty, tw, th), "FPS", _live.Fps > 0 ? _live.Fps.ToString("0") : "–", "avg", FpsColor(_live.Fps));
            StatTile(g, new RectangleF(inner.X + (tw + tg), ty, tw, th), "1% low", _live.Low1 > 0 ? _live.Low1.ToString("0") : "–", "FPS", FpsColor(_live.Low1));
            StatTile(g, new RectangleF(inner.X + 2 * (tw + tg), ty, tw, th), "Frame time", _live.FrameMs > 0 ? _live.FrameMs.ToString("0.0") : "–", "ms", Theme.Text);
            StatTile(g, new RectangleF(inner.X + 3 * (tw + tg), ty, tw, th), "Stutters", _live.StuttersPerMin.ToString("0"), "/ min", _live.StuttersPerMin > 3 ? Theme.Warn : Theme.Text);

            // Chart
            float cy = ty + th + 14 * S;
            float bottomBlock = 150 * S;
            float chartH = Math.Max(90 * S, Math.Min(170 * S, inner.Bottom - bottomBlock - 150 * S - cy));
            Gfx.FrameChart(g, new RectangleF(inner.X, cy, inner.Width, chartH), S, _live.Chart, ChartMax(_live.Chart), Theme.Accent);
            SectionLabel(g, "Frame time · last 8 s", inner.Right - 150 * S, cy + 10 * S);

            // Bottleneck
            float by = cy + chartH + 16 * S;
            SectionLabel(g, "Current bottleneck", inner.X, by);
            DrawDiagnosisBlock(g, _live.Diag, inner.X, by + 20 * S, inner.Width, true);

            // Goal + button
            float gy = inner.Bottom - bottomBlock + 6 * S;
            SectionLabel(g, "Goal", inner.X, gy);
            Segmented(g, new RectangleF(inner.X, gy + 20 * S, inner.Width, 38 * S));
            Gfx.Text(g, GoalDescription(_goal), Theme.Font(12 * S), Theme.TextDim, inner.X, gy + 64 * S);
            bool ready = _hw != null && _engine != null;
            Button(g, "optimise", new RectangleF(inner.X, inner.Bottom - 50 * S, inner.Width, 50 * S),
                ready ? "Optimise now" : "Scanning your PC…", Btn.Primary, () => { if (ready) StartOptimisation(); }, Glyph.Bolt, 15.5f);
        }

        static float ChartMax(IList<float> v)
        {
            float m = 20f;
            if (v != null) foreach (var x in v) if (x > m) m = x;
            return m <= 34 ? 40f : m <= 70 ? 80f : Math.Min(250f, m * 1.1f);
        }

        void Segmented(Graphics g, RectangleF r)
        {
            var goals = new[] { Goal.Balanced, Goal.MaxFps, Goal.Quality, Goal.Smooth };
            Gfx.Fill(g, r, 10 * S, Theme.A(Theme.Bg, 170));
            Gfx.Stroke(g, r, 10 * S, Theme.Border, 1f);
            float w = r.Width / goals.Length;
            for (int i = 0; i < goals.Length; i++)
            {
                var seg = new RectangleF(r.X + i * w + 3 * S, r.Y + 3 * S, w - 6 * S, r.Height - 6 * S);
                bool sel = goals[i] == _goal;
                string id = "goal" + i;
                if (sel) Gfx.Gradient(g, seg, 8 * S, Theme.A(Theme.Accent, 60), Theme.A(Theme.Accent2, 60), 0);
                else if (_hover == id) Gfx.Fill(g, seg, 8 * S, Theme.A(Theme.SurfaceHi, 200));
                if (sel) Gfx.Stroke(g, seg, 8 * S, Theme.A(Theme.Accent, 150), 1f);
                Gfx.TextCenter(g, OptimisationReport.GoalName(goals[i]), Theme.Font(13 * S, sel), sel ? Theme.Text : Theme.TextDim, seg);
                var gi = goals[i];
                AddHit(id, seg, () => _goal = gi);
            }
        }

        static string GoalDescription(Goal g)
        {
            switch (g)
            {
                case Goal.MaxFps: return "Highest frame rate. Accepts bigger visual trade-offs and never caps FPS.";
                case Goal.Quality: return "Keeps visuals high: only low-impact changes, just enough to reach the target.";
                case Goal.Smooth: return "Favours steady frame times and strong 1% lows; caps FPS when that helps.";
                default: return "Most performance for the least visual cost; stops lowering quality once the target is met.";
            }
        }

        void DrawNoGame(Graphics g, RectangleF r)
        {
            float cx = r.X + r.Width / 2, y = r.Y + r.Height * 0.2f;
            var circle = new RectangleF(cx - 42 * S, y, 84 * S, 84 * S);
            Gfx.Gradient(g, circle, 42 * S, Theme.A(Theme.Accent, 50), Theme.A(Theme.Accent2, 50), 45);
            Gfx.DrawIcon(g, Glyph.Play, new RectangleF(cx - 18 * S, y + 24 * S, 36 * S, 36 * S), Theme.Text, S);
            y += 108 * S;
            Gfx.TextCenter(g, "Start the game to begin", Theme.Font(22 * S, true, true), Theme.Text, new RectangleF(r.X, y, r.Width, 34 * S));
            y += 42 * S;
            string t = "GAPO reads the game's frame timings and your hardware, finds what is holding each frame back, then tests one change at a time — keeping what helps and undoing what doesn't.";
            var f = Theme.Font(13.5f * S);
            float w = Math.Min(r.Width, 520 * S);
            y += Gfx.Wrap(g, t, f, Theme.TextDim, cx - w / 2, y, w) + 24 * S;
            string exe = FindSandbox();
            if (exe != null)
                Button(g, "launch", new RectangleF(cx - 130 * S, y, 260 * S, 48 * S), "Launch GAPO Sandbox", Btn.Primary, () => LaunchSandbox(exe), Glyph.Play);
            else
                Gfx.TextCenter(g, "Open GAPO-Sandbox.exe first — GAPO connects automatically.", Theme.Font(13 * S, true), Theme.Warn, new RectangleF(r.X, y, r.Width, 30 * S));
            Gfx.TextCenter(g, "Nothing is injected into the game. GAPO only edits its settings file, one reversible change at a time.", Theme.Font(12 * S), Theme.TextFaint, new RectangleF(r.X, r.Bottom - 30 * S, r.Width, 24 * S));
        }

        static string FindSandbox()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var p in new[] { Path.Combine(dir, "GAPO-Sandbox.exe"), Path.Combine(dir, @"..\..\..\..\Sandbox\bin\Release\net462\GAPO-Sandbox.exe") })
                if (File.Exists(p)) return Path.GetFullPath(p);
            return null;
        }

        void LaunchSandbox(string exe)
        {
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); Toast("Starting the sandbox…"); }
            catch (Exception ex) { Toast("Couldn't start the sandbox: " + ex.Message); }
        }

        // ================================================================= your PC card

        void DrawPcCard(Graphics g, RectangleF r)
        {
            Gfx.Card(g, r, S);
            float p = 20 * S, x = r.X + p, y = r.Y + p, w = r.Width - 2 * p;
            SectionLabel(g, "Your PC", x, y);
            if (_hw != null)
            {
                string cls = OptimisationReport.ClassName(_hw.Class);
                var f = Theme.Font(11 * S, true);
                float cw = Gfx.PillWidth(g, cls, f, S);
                Gfx.Pill(g, new RectangleF(r.Right - p - cw, y - 5 * S, cw, 24 * S), _hw.Class == PcClass.Entry ? Theme.Warn : _hw.Class == PcClass.Performance ? Theme.Good : Theme.Info, cls, f);
            }
            y += 30 * S;
            if (_hw == null)
            {
                Gfx.Text(g, "Scanning " + _scanStep + "…", Theme.Font(13 * S), Theme.TextDim, x, y);
                y += 30 * S;
                double t = _clock.Elapsed.TotalSeconds;
                for (int i = 0; i < 7; i++)
                {
                    float a = (float)(0.5 + 0.5 * Math.Sin(t * 4 - i * 0.6));
                    Gfx.Fill(g, new RectangleF(x, y, 30 * S, 30 * S), 8 * S, Theme.A(Theme.SurfaceHi, (int)(140 + 80 * a)));
                    Gfx.Fill(g, new RectangleF(x + 42 * S, y + 2 * S, w * 0.55f, 11 * S), 5 * S, Theme.A(Theme.SurfaceHi, (int)(140 + 80 * a)));
                    Gfx.Fill(g, new RectangleF(x + 42 * S, y + 19 * S, w * 0.75f, 9 * S), 4 * S, Theme.A(Theme.SurfaceHi, (int)(100 + 60 * a)));
                    y += 50 * S;
                }
                return;
            }
            float rowGap = Math.Max(46 * S, Math.Min(58 * S, (r.Bottom - y - 60 * S) / 7));
            foreach (var row in PcRows())
            {
                if (y + rowGap > r.Bottom - 40 * S) break;
                var ib = new RectangleF(x, y + 2 * S, 32 * S, 32 * S);
                Gfx.Fill(g, ib, 9 * S, Theme.A(Theme.Accent, 26));
                Gfx.DrawIcon(g, row.Item1, new RectangleF(ib.X + 7 * S, ib.Y + 7 * S, 18 * S, 18 * S), Theme.Accent, S);
                Gfx.Text(g, Clip(g, row.Item2, Theme.Font(13 * S, true), w - 46 * S), Theme.Font(13 * S, true), Theme.Text, x + 44 * S, y + 1 * S);
                Gfx.Text(g, Clip(g, row.Item3, Theme.Font(11.5f * S), w - 46 * S), Theme.Font(11.5f * S), Theme.TextDim, x + 44 * S, y + 20 * S);
                y += rowGap;
            }
            Gfx.Text(g, "Scanned once in " + (_hw.ScanMs / 1000).ToString("0.0") + " s — never while a game is being measured.", Theme.Font(11 * S), Theme.TextFaint, x, r.Bottom - p - 12 * S);
        }

        IEnumerable<Tuple<Glyph, string, string>> PcRows()
        {
            var c = _hw.Cpu;
            yield return Tuple.Create(Glyph.Cpu, c.Name, c.Cores + " cores / " + c.Logical + " threads" + (c.Hybrid ? " · hybrid " + c.PerformanceCores + "P+" + c.EfficiencyCores + "E" : "") + (c.MaxMhz > 0 ? " · " + (c.MaxMhz / 1000.0).ToString("0.0#") + " GHz" : "") + (c.L3KB > 0 ? " · " + c.L3KB / 1024 + " MB L3" : ""));
            var gpu = _hw.PrimaryGpu;
            yield return Tuple.Create(Glyph.Gpu, gpu.Name, (gpu.VramBytes > 0 ? OptimisationReport.Gb(gpu.VramBytes) + (gpu.Integrated ? " shared" : " VRAM") : "VRAM not reported") + (gpu.Integrated ? " · integrated" : gpu.Virtual ? " · virtual adapter" : " · dedicated") + (gpu.DriverVersion.Length > 0 ? " · driver " + gpu.DriverVersion : ""));
            var m = _hw.Memory;
            string mod = m.Modules.Count > 0 ? m.Modules.Count + "× " + OptimisationReport.Gb(m.Modules[0].CapacityBytes) + (m.Modules[0].Type.Length > 0 ? " " + m.Modules[0].Type : "") + (m.Modules[0].ConfiguredMts > 0 ? "-" + m.Modules[0].ConfiguredMts : "") + " · " : "";
            yield return Tuple.Create(Glyph.Ram, OptimisationReport.Gb(m.TotalBytes) + " memory", mod + m.ChannelMode);
            var game = _hw.Storage.FirstOrDefault(s => s.Role.Contains("Game")) ?? _hw.Storage.FirstOrDefault();
            if (game != null) yield return Tuple.Create(Glyph.Disk, game.Drive + " " + (game.MediaType == "Unknown" ? "drive" : game.MediaType) + (game.BusType.Length > 0 ? " · " + game.BusType : ""), OptimisationReport.Gb(game.FreeBytes) + " free of " + OptimisationReport.Gb(game.TotalBytes) + (game.Model.Length > 0 ? " · " + game.Model : ""));
            var d = _hw.Display;
            yield return Tuple.Create(Glyph.Display, d.Width + " × " + d.Height + " @ " + (d.RefreshUnknown ? "? Hz" : d.RefreshHz + " Hz"), d.Monitors + " monitor" + (d.Monitors == 1 ? "" : "s") + " · " + d.ScalePct + "% scaling" + (d.RefreshUnknown ? " · refresh not reported" : ""));
            var p = _hw.Power;
            yield return Tuple.Create(Glyph.Power, p.HasBattery ? (p.OnAc ? "Laptop · plugged in" : "Laptop · on battery") : "Desktop · mains power", "Plan: " + p.PlanName + (p.Overlay.Length > 0 ? " · " + p.Overlay : "") + (p.BatteryPct >= 0 ? " · " + p.BatteryPct + "%" : ""));
            var o = _hw.Os;
            yield return Tuple.Create(Glyph.Windows, o.Name + (o.Version.Length > 0 ? " " + o.Version : ""), "Build " + o.Build + " · Game Mode " + (o.GameMode == false ? "off" : "on") + (o.Hags.HasValue ? " · HAGS " + (o.Hags.Value ? "on" : "off") : ""));
        }

        static string Clip(Graphics g, string s, Font f, float w)
        {
            if (Gfx.Measure(g, s, f).Width <= w) return s;
            while (s.Length > 3 && Gfx.Measure(g, s + "…", f).Width > w) s = s.Substring(0, s.Length - 1);
            return s.TrimEnd() + "…";
        }

        // ================================================================= running

        void StartOptimisation()
        {
            if (_engine == null || !_link.Connected) return;
            _engine.Start(_goal);
            _view = View.Running;
            _details = false;
            Invalidate();
        }

        void DrawRunning(Graphics g, RectangleF c)
        {
            float gap = 18 * S;
            var hero = new RectangleF(c.X, c.Y, c.Width * 0.635f, c.Height);
            var side = new RectangleF(hero.Right + gap, c.Y, c.Right - hero.Right - gap, c.Height);
            Gfx.Card(g, hero, S, true);
            float p = 22 * S;
            var inner = new RectangleF(hero.X + p, hero.Y + p, hero.Width - 2 * p, hero.Height - 2 * p);
            var st = _status ?? new EngineStatus();

            // Progress ring + phase
            float ring = 118 * S;
            var rr = new RectangleF(inner.X + 6 * S, inner.Y + 6 * S, ring, ring);
            float phase = st.Measuring ? 0 : (float)(_clock.Elapsed.TotalSeconds * 140 % 360);
            Gfx.Ring(g, rr, S, (float)st.Progress, 9 * S, false, 0);
            if (!st.Measuring) Gfx.Ring(g, new RectangleF(rr.X + 16 * S, rr.Y + 16 * S, rr.Width - 32 * S, rr.Height - 32 * S), S, 0.12f, 3 * S, true, phase);
            Gfx.TextCenter(g, (st.Progress * 100).ToString("0") + "%", Theme.Font(24 * S, true, true), Theme.Text, rr);
            float tx = rr.Right + 24 * S, tw = inner.Right - tx;
            SectionLabel(g, PhaseLabel(st.Phase), tx, inner.Y + 8 * S);
            float ty = inner.Y + 28 * S;
            ty += Gfx.Wrap(g, st.Title, Theme.Font(18 * S, true, true), Theme.Text, tx, ty, tw) + 6 * S;
            string detail = st.Detail ?? "";
            if (detail.Length > 240) detail = detail.Substring(0, 237) + "…";
            ty += Gfx.Wrap(g, detail, Theme.Font(12.5f * S), Theme.TextDim, tx, ty, tw) + 8 * S;
            if (st.Measuring)
            {
                var f = Theme.Font(11.5f * S, true);
                string m = "Measuring — GAPO is keeping quiet";
                float w = Gfx.PillWidth(g, m, f, S) + 10 * S;
                var pr = new RectangleF(tx, Math.Max(ty, rr.Bottom - 26 * S), w, 26 * S);
                Gfx.Pill(g, pr, Theme.Accent, "", f);
                Gfx.Dot(g, pr.X + 13 * S, pr.Y + 13 * S, 4 * S, Theme.Accent);
                Gfx.Text(g, m, f, Theme.Accent, pr.X + 23 * S, pr.Y + 5 * S);
            }

            // Chart
            float cy = Math.Max(rr.Bottom, ty + 30 * S) + 18 * S;
            float chartH = 130 * S;
            Gfx.FrameChart(g, new RectangleF(inner.X, cy, inner.Width, chartH), S, _live.Chart, ChartMax(_live.Chart), Theme.Accent);
            SectionLabel(g, "Live frame time", inner.Right - 118 * S, cy + 10 * S);

            // Timeline
            float ly = cy + chartH + 18 * S;
            SectionLabel(g, "One change at a time", inner.X, ly);
            ly += 22 * S;
            var rep = _engine != null ? _engine.Report : null;
            var logs = rep != null ? rep.Experiments.ToList() : new List<ExperimentLog>();
            float rowH = 38 * S;
            int fit = Math.Max(1, (int)((inner.Bottom - 56 * S - ly) / rowH));
            bool testingNow = st.Measuring && st.Phase != Phase.Baseline && st.Phase != Phase.Validating;
            int show = Math.Min(logs.Count, testingNow ? fit - 1 : fit);
            if (logs.Count > show) { Gfx.Text(g, "+" + (logs.Count - show) + " earlier", Theme.Font(11.5f * S), Theme.TextFaint, inner.Right - 80 * S, ly - 22 * S); }
            foreach (var l in logs.Skip(logs.Count - show)) { TimelineRow(g, new RectangleF(inner.X, ly, inner.Width, rowH - 6 * S), l); ly += rowH; }
            if (testingNow) TimelineCurrent(g, new RectangleF(inner.X, ly, inner.Width, rowH - 6 * S), st.Title);

            Button(g, "stop", new RectangleF(inner.Right - 150 * S, inner.Bottom - 40 * S, 150 * S, 40 * S), "Stop", Btn.Ghost, () => { if (_engine != null) _engine.Cancel(); }, Glyph.Stop, 13);
            Gfx.Text(g, "Your game stays playable while GAPO tests.", Theme.Font(12 * S), Theme.TextFaint, inner.X, inner.Bottom - 28 * S);

            // Side: diagnosis
            Gfx.Card(g, side, S);
            float sx = side.X + 20 * S, sy = side.Y + 20 * S, sw = side.Width - 40 * S;
            SectionLabel(g, "Diagnosis", sx, sy);
            sy += 24 * S;
            var diag = st.Diagnosis ?? _live.Diag;
            sy += DrawDiagnosisBlock(g, diag, sx, sy, sw, false) + 10 * S;
            if (diag != null && diag.EngineLimitation != null)
            {
                sy += InfoBox(g, sx, sy, sw, Glyph.Info, "Engine limitation", diag.EngineLimitation, Theme.Info) + 10 * S;
            }
            if (diag != null)
            {
                SectionLabel(g, "Frame budget", sx, sy); sy += 22 * S;
                sy += KeyValue(g, sx, sy, sw, "Target", diag.TargetFps.ToString("0") + " FPS (" + KnowledgeBase.F(diag.BudgetMs) + " ms per frame)");
                sy += KeyValue(g, sx, sy, sw, "Frame work now", KnowledgeBase.F(diag.WorkMs) + " ms");
                sy += KeyValue(g, sx, sy, sw, "Still to save", diag.RequiredSavingMs > 0 ? KnowledgeBase.F(diag.RequiredSavingMs) + " ms" : "nothing — target met");
            }
            if (rep != null && rep.NoiseFloorPct > 0 && sy < side.Bottom - 60 * S)
            {
                sy += 8 * S;
                SectionLabel(g, "Measurement precision", sx, sy); sy += 22 * S;
                KeyValue(g, sx, sy, sw, "Noise floor (A/A test)", "±" + rep.NoiseFloorPct.ToString("0.0") + "%");
            }
        }

        static string PhaseLabel(Phase p)
        {
            switch (p)
            {
                case Phase.Preparing: return "Preparing";
                case Phase.Baseline: return "Step 1 · Observe";
                case Phase.Diagnosing: return "Step 2 · Diagnose";
                case Phase.Testing: return "Step 3 · Test one change";
                case Phase.Pacing: return "Step 4 · Frame pacing";
                case Phase.Validating: return "Step 5 · Verify";
                default: return p.ToString();
            }
        }

        float KeyValue(Graphics g, float x, float y, float w, string k, string v)
        {
            Gfx.Text(g, k, Theme.Font(12.5f * S), Theme.TextDim, x, y);
            Gfx.TextRight(g, v, Theme.Font(12.5f * S, true), Theme.Text, x + w, y);
            return 22 * S;
        }

        float InfoBox(Graphics g, float x, float y, float w, Glyph icon, string title, string text, Color c)
        {
            var f = Theme.Font(12 * S);
            float th = Gfx.WrapHeight(g, text, f, w - 46 * S);
            var r = new RectangleF(x, y, w, th + 40 * S);
            Gfx.Fill(g, r, 12 * S, Theme.A(c, 22));
            Gfx.Stroke(g, r, 12 * S, Theme.A(c, 70), 1f);
            Gfx.DrawIcon(g, icon, new RectangleF(x + 12 * S, y + 12 * S, 18 * S, 18 * S), c, S);
            Gfx.Text(g, title, Theme.Font(12.5f * S, true), Theme.Text, x + 38 * S, y + 11 * S);
            Gfx.Wrap(g, text, f, Theme.TextDim, x + 38 * S, y + 31 * S, w - 46 * S);
            return r.Height;
        }

        void TimelineRow(Graphics g, RectangleF r, ExperimentLog l)
        {
            Gfx.Fill(g, r, 10 * S, Theme.A(Theme.Bg, 130));
            Color c = l.Decision == Decision.Kept ? Theme.Good : l.Decision == Decision.Reverted ? Theme.Warn : Theme.Bad;
            Glyph ic = l.Decision == Decision.Kept ? Glyph.Check : l.Decision == Decision.Reverted ? Glyph.Revert : Glyph.Cross;
            var cr = new RectangleF(r.X + 8 * S, r.Y + (r.Height - 22 * S) / 2, 22 * S, 22 * S);
            Gfx.Fill(g, cr, 11 * S, Theme.A(c, 40));
            Gfx.DrawIcon(g, ic, new RectangleF(cr.X + 3 * S, cr.Y + 3 * S, 16 * S, 16 * S), c, S);
            var f = Theme.Font(13 * S, true);
            string chip = l.Decision == Decision.Kept ? (l.Kind == ChangeKind.Hitch ? "no more freezes" : l.Kind == ChangeKind.Pacing ? "smoother" : OptimisationReport.Signed(l.GainPct) + "%") : l.Decision == Decision.Reverted ? "rolled back" : "failed";
            var cf = Theme.Font(11.5f * S, true);
            float cw = Gfx.PillWidth(g, chip, cf, S);
            Gfx.Text(g, Clip(g, l.Title, f, r.Width - cw - 120 * S), f, Theme.Text, cr.Right + 10 * S, r.Y + (r.Height - 17 * S) / 2);
            Gfx.Pill(g, new RectangleF(r.Right - cw - 8 * S, r.Y + (r.Height - 22 * S) / 2, cw, 22 * S), c, chip, cf);
            if (l.Kind == ChangeKind.GameSetting) Gfx.CostDots(g, r.Right - cw - 64 * S, r.Y + r.Height / 2, S, l.VisualLoss);
        }

        void TimelineCurrent(Graphics g, RectangleF r, string title)
        {
            Gfx.Fill(g, r, 10 * S, Theme.A(Theme.Accent, 22));
            Gfx.Stroke(g, r, 10 * S, Theme.A(Theme.Accent, 90), 1f);
            var cr = new RectangleF(r.X + 8 * S, r.Y + (r.Height - 22 * S) / 2, 22 * S, 22 * S);
            Gfx.Ring(g, cr, S, 0.3f, 2.5f * S, true, (float)(_clock.Elapsed.TotalSeconds * 200 % 360));
            Gfx.Text(g, Clip(g, title, Theme.Font(13 * S, true), r.Width - 140 * S), Theme.Font(13 * S, true), Theme.Text, cr.Right + 10 * S, r.Y + (r.Height - 17 * S) / 2);
            Gfx.TextRight(g, "measuring…", Theme.Font(12 * S), Theme.Accent, r.Right - 12 * S, r.Y + (r.Height - 15 * S) / 2);
        }

        // ================================================================= results

        void DrawResults(Graphics g, RectangleF c)
        {
            var rep = _engine != null ? _engine.Report : null;
            if (rep == null || rep.Baseline == null || rep.Final == null) { _view = View.Home; return; }
            float heroH = _details ? 168 * S : 262 * S;
            var hero = new RectangleF(c.X, c.Y, c.Width, heroH);
            Gfx.Card(g, hero, S, true);
            float p = 24 * S;
            double t = _resultsShownAt < 0 ? 1 : Math.Min(1, (_clock.Elapsed.TotalMilliseconds - _resultsShownAt) / 1300.0);
            double ease = 1 - Math.Pow(1 - t, 3);

            // Big number
            double gain = rep.FpsGainPct * ease;
            string big = OptimisationReport.Signed(gain) + "%";
            var fBig = Theme.Font((_details ? 58 : 84) * S, true, true);
            float bx = hero.X + p, by = hero.Y + (_details ? 14 * S : 20 * S);
            Gfx.GradientText(g, big, fBig, bx, by, gain >= 0 ? Theme.Accent : Theme.Warn, gain >= 0 ? Theme.Accent2 : Theme.Bad);
            var bigSize = Gfx.MeasurePath(OptimisationReport.Signed(rep.FpsGainPct) + "%", fBig);
            float labelY = by + bigSize.Height + 6 * S;
            Gfx.Text(g, rep.FpsGainPct >= 0 ? "higher average FPS" : "change in average FPS", Theme.Font(15 * S, true), Theme.Text, bx + 4 * S, labelY);
            Gfx.Text(g, rep.WorkSpeedup.ToString("0.0") + "× less work per frame", Theme.Font(12.5f * S), Theme.TextDim, bx + 4 * S, labelY + 22 * S);

            // Metrics
            float mx = hero.X + Math.Max(bigSize.Width + 70 * S, hero.Width * 0.36f), mw = hero.Right - p - mx;
            float my = hero.Y + p - 4 * S, rowH = _details ? 25 * S : 34 * S;
            MetricRow(g, mx, my, mw, "Average FPS", rep.Baseline.AvgFps, rep.Final.AvgFps, "0", true, ease); my += rowH;
            MetricRow(g, mx, my, mw, "1% low FPS", rep.Baseline.Low1Fps, rep.Final.Low1Fps, "0", true, ease); my += rowH;
            if (rep.BaselineHitches != null && rep.FinalHitches != null) { MetricRow(g, mx, my, mw, "Stutters per minute", rep.BaselineHitches.StuttersPerMin, rep.FinalHitches.StuttersPerMin, "0.0", false, ease); my += rowH; }
            if (!_details) { MetricRow(g, mx, my, mw, "Frame work (ms)", rep.Baseline.MedianWorkMs, rep.Final.MedianWorkMs, "0.0", false, ease); my += rowH; }

            // Summary + buttons
            float fy = hero.Bottom - p - 44 * S;
            if (!_details)
            {
                string summary = "Kept " + rep.Kept + " change" + (rep.Kept == 1 ? "" : "s") + (rep.Reverted > 0 ? ", rolled back " + rep.Reverted + " that didn't prove themselves" : "") + ". " + rep.PacingNote;
                Gfx.Wrap(g, summary, Theme.Font(12.5f * S), Theme.TextDim, hero.X + p, fy - 34 * S, hero.Width - 2 * p);
            }
            float bw = 190 * S, bh = 42 * S, gx = 10 * S;
            Button(g, "more", new RectangleF(hero.X + p, fy, bw + 20 * S, bh), _details ? "Show less" : "Show more options", Btn.Primary, () => { _details = !_details; _scroll = 0; }, _details ? Glyph.ChevronUp : Glyph.ChevronDown, 14);
            float x2 = hero.X + p + bw + 20 * S + gx;
            Button(g, "revert", new RectangleF(x2, fy, 140 * S, bh), "Revert all", Btn.Secondary, RevertAll, Glyph.Revert, 13.5f);
            Button(g, "again", new RectangleF(x2 + 140 * S + gx, fy, 160 * S, bh), "Optimise again", Btn.Secondary, () => { _view = View.Home; _resultsShownAt = -1; }, Glyph.Bolt, 13.5f);
            Button(g, "save", new RectangleF(x2 + 300 * S + 2 * gx, fy, 150 * S, bh), "Save report", Btn.Ghost, SaveReport, Glyph.Doc, 13.5f);

            var below = new RectangleF(c.X, hero.Bottom + 16 * S, c.Width, c.Bottom - hero.Bottom - 16 * S);
            if (_details) DrawDetails(g, below, rep);
            else DrawResultsOverview(g, below, rep);
        }

        void MetricRow(Graphics g, float x, float y, float w, string label, double before, double after, string fmt, bool higherIsBetter, double ease)
        {
            var f = Theme.Font(13.5f * S);
            var fb = Theme.Font(15 * S, true);
            Gfx.Text(g, label, f, Theme.TextDim, x, y + 2 * S);
            double shown = before + (after - before) * ease;
            bool better = higherIsBetter ? after > before * 1.01 : after < before * 0.99;
            bool worse = higherIsBetter ? after < before * 0.99 : after > before * 1.01;
            Color c = better ? Theme.Good : worse ? Theme.Bad : Theme.TextDim;
            string delta;
            if (before > 0.05) delta = OptimisationReport.Signed((after / before - 1) * 100) + "%";
            else delta = after <= 0.05 ? "±0" : "new";
            var cf = Theme.Font(11.5f * S, true);
            float cw = Gfx.PillWidth(g, delta, cf, S);
            Gfx.Pill(g, new RectangleF(x + w - cw, y, cw, 24 * S), c, delta, cf);
            float vx = x + w - cw - 16 * S;
            string after_s = shown.ToString(fmt);
            float aw = Gfx.Measure(g, after_s, fb).Width;
            Gfx.Text(g, after_s, fb, Theme.Text, vx - aw, y);
            Gfx.Text(g, "→", f, Theme.TextFaint, vx - aw - 22 * S, y + 1 * S);
            string before_s = before.ToString(fmt);
            float bw = Gfx.Measure(g, before_s, f).Width;
            Gfx.Text(g, before_s, f, Theme.TextFaint, vx - aw - 30 * S - bw, y + 1 * S);
        }

        void DrawResultsOverview(Graphics g, RectangleF r, OptimisationReport rep)
        {
            float gap = 18 * S;
            var left = new RectangleF(r.X, r.Y, r.Width * 0.58f, r.Height);
            var right = new RectangleF(left.Right + gap, r.Y, r.Right - left.Right - gap, r.Height);
            Gfx.Card(g, left, S);
            float p = 20 * S;
            SectionLabel(g, "Before vs after · same benchmark flight", left.X + p, left.Y + p);
            float lx = left.Right - p - 190 * S;
            Gfx.Dot(g, lx, left.Y + p + 7 * S, 4 * S, Theme.A(Theme.Bad, 220)); Gfx.Text(g, "before", Theme.Font(11.5f * S), Theme.TextDim, lx + 8 * S, left.Y + p);
            Gfx.Dot(g, lx + 70 * S, left.Y + p + 7 * S, 4 * S, Theme.Accent); Gfx.Text(g, "after", Theme.Font(11.5f * S), Theme.TextDim, lx + 78 * S, left.Y + p);
            var chart = new RectangleF(left.X + p, left.Y + p + 26 * S, left.Width - 2 * p, left.Height - 2 * p - 26 * S);
            var before = Resample(rep.BaselineTrace, 140);
            var after = Resample(rep.FinalTrace, 140);
            float max = Math.Max(ChartMax(before), ChartMax(after));
            Gfx.FrameChart(g, chart, S, after, max, Theme.Accent, before, Theme.A(Theme.Bad, 200));

            Gfx.Card(g, right, S);
            float x = right.X + p, y = right.Y + p, w = right.Width - 2 * p;
            SectionLabel(g, "What GAPO changed", x, y);
            y += 24 * S;
            foreach (var e in rep.Experiments.Where(e => e.Decision == Decision.Kept))
            {
                if (y > right.Bottom - 60 * S) break;
                Gfx.DrawIcon(g, Glyph.Check, new RectangleF(x, y + 1 * S, 16 * S, 16 * S), Theme.Good, S);
                var f = Theme.Font(12.5f * S, true);
                string val = e.Kind == ChangeKind.Hitch ? "freezes gone" : e.Kind == ChangeKind.Pacing ? "steadier" : e.Kind == ChangeKind.QualityRestore ? "quality back" : OptimisationReport.Signed(e.GainPct) + "%";
                Gfx.Text(g, Clip(g, e.Title, f, w - 110 * S), f, Theme.Text, x + 24 * S, y);
                Gfx.TextRight(g, val, Theme.Font(12.5f * S, true), Theme.Good, x + w, y);
                y += 26 * S;
            }
            int rev = rep.Experiments.Count(e => e.Decision == Decision.Reverted);
            if (rev > 0 && y < right.Bottom - 40 * S)
            {
                Gfx.DrawIcon(g, Glyph.Revert, new RectangleF(x, y + 1 * S, 16 * S, 16 * S), Theme.Warn, S);
                Gfx.Text(g, rev + " tested and rolled back (no proven benefit)", Theme.Font(12.5f * S), Theme.TextDim, x + 24 * S, y);
                y += 26 * S;
            }
            Gfx.Text(g, "Open “Show more options” for every measurement.", Theme.Font(11.5f * S), Theme.TextFaint, x, right.Bottom - p - 12 * S);
        }

        static List<float> Resample(List<float> v, int n)
        {
            var r = new List<float>();
            if (v == null || v.Count == 0) return r;
            for (int i = 0; i < n; i++)
            {
                int a = i * v.Count / n, b = Math.Max(a + 1, (i + 1) * v.Count / n);
                float m = 0;
                for (int k = a; k < b && k < v.Count; k++) m = Math.Max(m, v[k]);
                r.Add(m);
            }
            return r;
        }

        void RevertAll()
        {
            if (_engine == null) return;
            _engine.RevertAll();
            Toast("Every change was reverted — the game is back to its original settings.");
            _view = View.Home;
            _resultsShownAt = -1;
        }

        void SaveReport()
        {
            var rep = _engine != null ? _engine.Report : null;
            if (rep == null) return;
            try
            {
                string path = rep.Save();
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = false });
                Toast("Report saved to " + path);
            }
            catch (Exception ex) { Toast("Couldn't save the report: " + ex.Message); }
        }

        // ================================================================= details (Show more options)

        static readonly string[] Tabs = { "What I changed", "Diagnosis", "Your PC", "Tips", "Live analyser" };

        void DrawDetails(Graphics g, RectangleF r, OptimisationReport rep)
        {
            // Tabs
            float tx = r.X, th = 36 * S;
            var ft = Theme.Font(13 * S, true);
            for (int i = 0; i < Tabs.Length; i++)
            {
                string label = Tabs[i] + (i == 3 && rep.Recommendations.Count > 0 ? " (" + rep.Recommendations.Count + ")" : "");
                float tabW = Gfx.Measure(g, label, ft).Width + 30 * S;
                var tr = new RectangleF(tx, r.Y, tabW, th);
                bool sel = i == _tab;
                string id = "tab" + i;
                if (sel) Gfx.Gradient(g, tr, 10 * S, Theme.A(Theme.Accent, 55), Theme.A(Theme.Accent2, 55), 0);
                else if (_hover == id) Gfx.Fill(g, tr, 10 * S, Theme.A(Theme.SurfaceHi, 220));
                if (sel) Gfx.Stroke(g, tr, 10 * S, Theme.A(Theme.Accent, 140), 1f);
                Gfx.TextCenter(g, label, ft, sel ? Theme.Text : Theme.TextDim, tr);
                int ti = i;
                AddHit(id, tr, () => { _tab = ti; _scroll = 0; });
                tx += tabW + 8 * S;
            }

            var area = new RectangleF(r.X, r.Y + th + 12 * S, r.Width, r.Height - th - 12 * S);
            Gfx.Card(g, area, S);
            var clip = new RectangleF(area.X + 1, area.Y + 1, area.Width - 2, area.Height - 2);
            _scrollArea = clip;
            var state = g.Save();
            using (var cp = Theme.Round(clip, 15 * S)) g.SetClip(cp);
            g.TranslateTransform(0, -_scroll);
            _clipArea = clip; _clipOffset = _scroll;
            float p = 22 * S;
            float x = area.X + p, y = area.Y + p, w = area.Width - 2 * p - 10 * S;
            float end;
            switch (_tab)
            {
                case 0: end = TabChanges(g, rep, x, y, w); break;
                case 1: end = TabDiagnosis(g, rep, x, y, w); break;
                case 2: end = TabPc(g, rep, x, y, w); break;
                case 3: end = TabTips(g, rep, x, y, w); break;
                default: end = TabLive(g, x, y, w); break;
            }
            _clipArea = null; _clipOffset = 0;
            g.Restore(state);
            _scrollMax = Math.Max(0, end + p - area.Bottom);
            if (_scroll > _scrollMax) _scroll = _scrollMax;
            if (_scrollMax > 0)
            {
                float trackH = area.Height - 24 * S;
                float thumbH = Math.Max(30 * S, trackH * area.Height / (area.Height + _scrollMax));
                float thumbY = area.Y + 12 * S + (trackH - thumbH) * (_scroll / _scrollMax);
                Gfx.Fill(g, new RectangleF(area.Right - 10 * S, thumbY, 4 * S, thumbH), 2 * S, Theme.A(Theme.TextFaint, 150));
            }
        }

        float TabChanges(Graphics g, OptimisationReport rep, float x, float y, float w)
        {
            Gfx.Wrap(g, "Every change was tested on its own, on the same benchmark flight, and kept only if the measured gain beat the ±" + rep.NoiseFloorPct.ToString("0.0") + "% measurement noise and was worth its visual cost for the " + OptimisationReport.GoalName(rep.Goal) + " goal.", Theme.Font(12.5f * S), Theme.TextDim, x, y, w);
            y += 44 * S;
            foreach (var e in rep.Experiments)
            {
                var fs = Theme.Font(12 * S);
                float hh = Gfx.WrapHeight(g, "Why: " + e.Hypothesis, fs, w - 60 * S) + Gfx.WrapHeight(g, e.Reason, fs, w - 60 * S);
                float cardH = 64 * S + hh + (e.Before != null && e.After != null && e.Kind != ChangeKind.Hitch ? 22 * S : 0);
                var r = new RectangleF(x, y, w, cardH);
                Gfx.Fill(g, r, 12 * S, Theme.A(Theme.Bg, 140));
                Gfx.Stroke(g, r, 12 * S, Theme.A(Theme.Border, 220), 1f);
                Color c = e.Decision == Decision.Kept ? Theme.Good : e.Decision == Decision.Reverted ? Theme.Warn : Theme.Bad;
                var ic = new RectangleF(x + 14 * S, y + 14 * S, 28 * S, 28 * S);
                Gfx.Fill(g, ic, 14 * S, Theme.A(c, 40));
                Gfx.TextCenter(g, e.Index.ToString(), Theme.Font(12.5f * S, true), c, ic);
                float cx = x + 56 * S;
                Gfx.Text(g, e.Title, Theme.Font(14 * S, true), Theme.Text, cx, y + 14 * S);
                string chip = e.Decision == Decision.Kept ? "Kept" : e.Decision == Decision.Reverted ? "Rolled back" : e.Decision.ToString();
                var cf = Theme.Font(11.5f * S, true);
                float cw = Gfx.PillWidth(g, chip, cf, S);
                Gfx.Pill(g, new RectangleF(x + w - cw - 14 * S, y + 14 * S, cw, 24 * S), c, chip, cf);
                if (e.Kind == ChangeKind.GameSetting)
                {
                    float dotsX = x + w - cw - 72 * S;
                    Gfx.TextRight(g, "visual cost", Theme.Font(11 * S), Theme.TextFaint, dotsX - 10 * S, y + 18 * S);
                    Gfx.CostDots(g, dotsX, y + 26 * S, S, e.VisualLoss);
                }
                float yy = y + 40 * S;
                yy += Gfx.Wrap(g, "Why: " + e.Hypothesis, fs, Theme.TextDim, cx, yy, w - 60 * S) + 4 * S;
                if (e.Before != null && e.After != null && e.Kind != ChangeKind.Hitch)
                {
                    string m = "Frame work " + KnowledgeBase.F(e.Before.MedianWorkMs) + " → " + KnowledgeBase.F(e.After.MedianWorkMs) + " ms (" + OptimisationReport.Signed(e.GainPct) + "%) · FPS " + e.Before.AvgFps.ToString("0") + " → " + e.After.AvgFps.ToString("0") + " · 1% low " + e.Before.Low1Fps.ToString("0") + " → " + e.After.Low1Fps.ToString("0") + (e.PredictedMs > 0 ? " · predicted −" + KnowledgeBase.F(e.PredictedMs) + " ms" : "");
                    Gfx.Text(g, m, Theme.Font(12 * S, true), Theme.Text, cx, yy);
                    yy += 22 * S;
                }
                Gfx.Wrap(g, e.Reason, fs, Theme.Mix(c, Theme.Text, 0.35f), cx, yy, w - 60 * S);
                y += cardH + 10 * S;
            }
            if (rep.NotTouched.Count > 0)
            {
                y += 8 * S;
                SectionLabel(g, "Considered but not changed", x, y);
                y += 24 * S;
                foreach (var n in rep.NotTouched)
                {
                    Gfx.DrawIcon(g, Glyph.Info, new RectangleF(x, y + 1 * S, 16 * S, 16 * S), Theme.Info, S);
                    y += Gfx.Wrap(g, n, Theme.Font(12.5f * S), Theme.TextDim, x + 26 * S, y, w - 26 * S) + 10 * S;
                }
            }
            return y;
        }

        float TabDiagnosis(Graphics g, OptimisationReport rep, float x, float y, float w)
        {
            float colW = (w - 20 * S) / 2;
            double scale = Math.Max(rep.BaselineDiagnosis.WorkMs, rep.BaselineDiagnosis.BudgetMs) * 1.1;
            float h1 = DiagColumn(g, "Before", rep.BaselineDiagnosis, rep.Baseline, x, y, colW, scale);
            float h2 = rep.FinalDiagnosis != null ? DiagColumn(g, "After", rep.FinalDiagnosis, rep.Final, x + colW + 20 * S, y, colW, scale) : 0;
            y += Math.Max(h1, h2) + 16 * S;

            var fd = rep.FinalDiagnosis;
            if (fd != null && fd.RequiredSavingMs > 0.1)
            {
                SectionLabel(g, "Why am I not reaching my target?", x, y); y += 24 * S;
                y += KeyValue(g, x, y, Math.Min(w, 520 * S), "Target", rep.TargetFps.ToString("0") + " FPS · budget " + KnowledgeBase.F(fd.BudgetMs) + " ms");
                y += KeyValue(g, x, y, Math.Min(w, 520 * S), "Current frame work", KnowledgeBase.F(fd.WorkMs) + " ms");
                y += KeyValue(g, x, y, Math.Min(w, 520 * S), "Required improvement", "≈ " + KnowledgeBase.F(fd.RequiredSavingMs) + " ms per frame");
                y += 8 * S;
                var rest = KnowledgeBase.Generate(rep.FinalSettings, rep.Final, _hw, false).OrderByDescending(c => c.Value).Take(4).ToList();
                int i = 1;
                foreach (var c in rest)
                {
                    Gfx.Text(g, i++ + ". " + c.Title, Theme.Font(13 * S, true), Theme.Text, x, y);
                    Gfx.Text(g, "expected −" + KnowledgeBase.F(c.PredictedMs) + " ms · visual impact " + (c.VisualLoss <= 1 ? "low" : c.VisualLoss <= 3 ? "medium" : "high"), Theme.Font(12 * S), Theme.TextDim, x + 20 * S, y + 20 * S);
                    y += 44 * S;
                }
            }
            else if (fd != null)
            {
                y += InfoBox(g, x, y, w, Glyph.Check, "Target reached", "Each frame now needs " + KnowledgeBase.F(fd.WorkMs) + " ms of work against a " + KnowledgeBase.F(fd.BudgetMs) + " ms budget (" + rep.TargetFps.ToString("0") + " FPS). The headroom absorbs busy scenes, and the frame cap turns the rest into lower CPU use and heat.", Theme.Good) + 14 * S;
            }
            if (rep.BaselineDiagnosis.EngineLimitation != null)
                y += InfoBox(g, x, y, w, Glyph.Info, "Engine limitation (category C)", rep.BaselineDiagnosis.EngineLimitation, Theme.Info) + 14 * S;

            SectionLabel(g, "Evidence", x, y); y += 24 * S;
            foreach (var e in rep.BaselineDiagnosis.Evidence)
            {
                Gfx.Dot(g, x + 5 * S, y + 9 * S, 3 * S, Theme.Accent);
                y += Gfx.Wrap(g, e, Theme.Font(12.5f * S), Theme.TextDim, x + 18 * S, y, w - 18 * S) + 8 * S;
            }
            foreach (var e in rep.BaselineDiagnosis.Secondary)
            {
                Gfx.DrawIcon(g, Glyph.Warn, new RectangleF(x, y + 1 * S, 14 * S, 14 * S), Theme.Warn, S);
                y += Gfx.Wrap(g, e, Theme.Font(12.5f * S), Theme.TextDim, x + 18 * S, y, w - 18 * S) + 8 * S;
            }
            y += 10 * S;
            SectionLabel(g, "Measurement quality", x, y); y += 24 * S;
            float kw = Math.Min(w, 560 * S);
            y += KeyValue(g, x, y, kw, "Noise floor (baseline A/A test)", "±" + rep.NoiseFloorPct.ToString("0.0") + "%");
            y += KeyValue(g, x, y, kw, "Frames measured", rep.MeasuredFrames + " in " + rep.Windows + " windows");
            y += KeyValue(g, x, y, kw, "GAPO CPU use (average / in windows)", rep.SelfCpuAvgPct.ToString("0.00") + "% / " + rep.SelfCpuInWindowsPct.ToString("0.00") + "%");
            y += KeyValue(g, x, y, kw, "GAPO memory", rep.SelfPrivateMB.ToString("0") + " MB");
            y += KeyValue(g, x, y, kw, "Time taken", (rep.Finished - rep.Started).TotalSeconds.ToString("0") + " s");
            return y;
        }

        float DiagColumn(Graphics g, string label, Diagnosis d, FrameStats st, float x, float y, float w, double scale)
        {
            float y0 = y;
            SectionLabel(g, label, x, y); y += 22 * S;
            Color tc = d.Primary == Bottleneck.TargetMet ? Theme.Good : Theme.Text;
            Gfx.Text(g, d.Title, Theme.Font(15 * S, true), tc, x, y); y += 26 * S;
            Gfx.StageBar(g, new RectangleF(x, y, w, 12 * S), S, d.Stages, d.WorkMs, d.BudgetMs, scale); y += 22 * S;
            y += Legend(g, d.Stages, x, y, w) + 8 * S;
            y += Gfx.Wrap(g, d.Summary, Theme.Font(12.5f * S), Theme.TextDim, x, y, w) + 8 * S;
            y += KeyValue(g, x, y, w, "Average / 1% / 0.1% low", st.AvgFps.ToString("0") + " / " + st.Low1Fps.ToString("0") + " / " + st.Low01Fps.ToString("0") + " FPS");
            y += KeyValue(g, x, y, w, "Frame-time variation", KnowledgeBase.F(st.StdDevFrameMs) + " ms");
            return y - y0;
        }

        float TabPc(Graphics g, OptimisationReport rep, float x, float y, float w)
        {
            if (_hw == null) return y;
            float colW = (w - 16 * S) / 2;
            int i = 0;
            float rowY = y, rowH = 0;
            foreach (var row in PcRows())
            {
                float cx = x + (i % 2) * (colW + 16 * S);
                var f = Theme.Font(12 * S);
                float h = 50 * S + Gfx.WrapHeight(g, row.Item3, f, colW - 64 * S);
                var r = new RectangleF(cx, rowY, colW, h);
                Gfx.Fill(g, r, 12 * S, Theme.A(Theme.Bg, 140));
                Gfx.Stroke(g, r, 12 * S, Theme.A(Theme.Border, 220), 1f);
                var ib = new RectangleF(cx + 14 * S, rowY + 14 * S, 34 * S, 34 * S);
                Gfx.Fill(g, ib, 9 * S, Theme.A(Theme.Accent, 26));
                Gfx.DrawIcon(g, row.Item1, new RectangleF(ib.X + 8 * S, ib.Y + 8 * S, 18 * S, 18 * S), Theme.Accent, S);
                Gfx.Text(g, Clip(g, row.Item2, Theme.Font(13.5f * S, true), colW - 70 * S), Theme.Font(13.5f * S, true), Theme.Text, cx + 60 * S, rowY + 14 * S);
                Gfx.Wrap(g, row.Item3, f, Theme.TextDim, cx + 60 * S, rowY + 34 * S, colW - 70 * S);
                rowH = Math.Max(rowH, h);
                if (i % 2 == 1) { rowY += rowH + 12 * S; rowH = 0; }
                i++;
            }
            if (i % 2 == 1) rowY += rowH + 12 * S;
            y = rowY + 8 * S;
            SectionLabel(g, "How GAPO used your hardware", x, y); y += 24 * S;
            foreach (var d in rep.HardwareDecisions)
            {
                Gfx.DrawIcon(g, Glyph.Spark, new RectangleF(x, y + 1 * S, 14 * S, 14 * S), Theme.Accent2, S);
                y += Gfx.Wrap(g, d, Theme.Font(12.5f * S), Theme.TextDim, x + 22 * S, y, w - 22 * S) + 8 * S;
            }
            y += 8 * S;
            SectionLabel(g, "PC class", x, y); y += 22 * S;
            y += Gfx.Wrap(g, OptimisationReport.ClassName(_hw.Class) + " — " + _hw.ClassReason + ".", Theme.Font(12.5f * S), Theme.TextDim, x, y, w) + 12 * S;
            if (_hw.Notes.Count > 0)
            {
                SectionLabel(g, "Could not be read", x, y); y += 22 * S;
                foreach (var n in _hw.Notes) y += Gfx.Wrap(g, n, Theme.Font(12 * S), Theme.TextFaint, x, y, w) + 6 * S;
            }
            return y;
        }

        float TabTips(Graphics g, OptimisationReport rep, float x, float y, float w)
        {
            Gfx.Wrap(g, "Things GAPO noticed but deliberately does not change for you — each one comes with the evidence behind it. GAPO never closes apps, edits the registry, touches BIOS settings or disables security software.", Theme.Font(12.5f * S), Theme.TextDim, x, y, w);
            y += 46 * S;
            if (rep.Recommendations.Count == 0)
            {
                y += InfoBox(g, x, y, w, Glyph.Check, "Nothing else to fix", "No background contention, power or hardware issues were found.", Theme.Good);
                return y;
            }
            int i = 0;
            foreach (var r in rep.Recommendations)
            {
                Color c = r.Severity == 2 ? Theme.Warn : r.Severity == 1 ? Theme.Accent : Theme.Info;
                Glyph ic = r.Severity == 2 ? Glyph.Warn : r.Severity == 1 ? Glyph.Spark : Glyph.Info;
                float bw = r.ActionLabel != null ? 190 * S : 0;
                var f = Theme.Font(12.5f * S);
                float th = Gfx.WrapHeight(g, r.Detail, f, w - 60 * S - bw);
                var box = new RectangleF(x, y, w, Math.Max(62 * S, th + 44 * S));
                Gfx.Fill(g, box, 12 * S, Theme.A(c, 18));
                Gfx.Stroke(g, box, 12 * S, Theme.A(c, 70), 1f);
                Gfx.DrawIcon(g, ic, new RectangleF(x + 16 * S, y + 16 * S, 20 * S, 20 * S), c, S);
                Gfx.Text(g, r.Title, Theme.Font(14 * S, true), Theme.Text, x + 48 * S, y + 13 * S);
                Gfx.Wrap(g, r.Detail, f, Theme.TextDim, x + 48 * S, y + 35 * S, w - 60 * S - bw);
                if (r.ActionLabel != null)
                {
                    string target = r.ActionTarget;
                    Button(g, "tip" + i, new RectangleF(box.Right - bw - 4 * S, y + (box.Height - 38 * S) / 2, bw - 12 * S, 38 * S), r.ActionLabel, Btn.Secondary, () => OpenSettings(target), null, 12.5f);
                }
                y += box.Height + 10 * S;
                i++;
            }
            if (rep.BackgroundApps.Count > 0)
            {
                y += 8 * S;
                SectionLabel(g, "Background activity while the game ran", x, y); y += 24 * S;
                foreach (var a in rep.BackgroundApps.Take(8))
                {
                    Gfx.Text(g, a.Name + (a.Processes > 1 ? " (" + a.Processes + " processes)" : ""), Theme.Font(12.5f * S, true), a.Protected ? Theme.TextFaint : Theme.Text, x, y);
                    Gfx.Text(g, a.Kind + (a.Protected ? " · never touched" : ""), Theme.Font(12 * S), Theme.TextFaint, x + w * 0.45f, y);
                    Gfx.TextRight(g, a.CpuPct.ToString("0.0") + "% CPU · " + OptimisationReport.Gb(a.MemBytes), Theme.Font(12.5f * S), Theme.TextDim, x + w, y);
                    y += 24 * S;
                }
            }
            return y;
        }

        void OpenSettings(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Toast("Couldn't open Windows Settings: " + ex.Message); }
        }

        float TabLive(Graphics g, float x, float y, float w)
        {
            var m = _monitor.Latest ?? new MonitorSample();
            Gfx.Wrap(g, "The Analyser view: live system state, sampled once a second from cheap Windows counters. Utilisation alone isn't a bottleneck — compare it with the frame-time breakdown.", Theme.Font(12.5f * S), Theme.TextDim, x, y, w);
            y += 44 * S;
            SectionLabel(g, "CPU load per logical processor", x, y); y += 24 * S;
            int n = m.CoreUsage.Length;
            if (n > 0)
            {
                float bw = Math.Min(46 * S, (w - (n - 1) * 8 * S) / n), bh = 90 * S;
                for (int i = 0; i < n; i++)
                {
                    var br = new RectangleF(x + i * (bw + 8 * S), y, bw, bh);
                    Gfx.Fill(g, br, 6 * S, Theme.A(Theme.Bg, 170));
                    float u = m.CoreUsage[i];
                    float fh = Math.Max(2 * S, bh * u);
                    Gfx.Gradient(g, new RectangleF(br.X, br.Bottom - fh, bw, fh), 6 * S, u > 0.9f ? Theme.Bad : Theme.Accent, u > 0.9f ? Theme.Warn : Theme.Accent2, 90);
                    Gfx.TextCenter(g, (u * 100).ToString("0"), Theme.Font(10.5f * S, true), Theme.Text, new RectangleF(br.X, br.Bottom + 2 * S, bw, 16 * S));
                }
                y += bh + 26 * S;
            }
            float kw = Math.Min(w, 600 * S);
            SectionLabel(g, "System", x, y); y += 22 * S;
            y += KeyValue(g, x, y, kw, "Total CPU", m.TotalCpuPct.ToString("0") + "%");
            y += KeyValue(g, x, y, kw, "Game CPU", m.GameCpuCores.ToString("0.00") + " cores (" + m.GameCpuPct.ToString("0") + "% of the PC)");
            y += KeyValue(g, x, y, kw, "Background apps CPU", m.BackgroundCpuPct.ToString("0") + "%");
            y += KeyValue(g, x, y, kw, "Memory in use", m.MemLoadPct + "% · " + OptimisationReport.Gb(m.AvailBytes) + " free");
            y += KeyValue(g, x, y, kw, "Game memory", OptimisationReport.Gb(m.GamePrivateBytes) + " private");
            y += KeyValue(g, x, y, kw, "Game disk activity", m.GameWriteMBps.ToString("0.0") + " MB/s write · " + m.GameReadMBps.ToString("0.0") + " MB/s read");
            y += 10 * S;
            SectionLabel(g, "Frame (last 3 s)", x, y); y += 22 * S;
            y += KeyValue(g, x, y, kw, "Average FPS", _live.Fps.ToString("0.0"));
            y += KeyValue(g, x, y, kw, "1% low / 0.1% low", _live.Low1.ToString("0.0") + " / " + _live.Low01.ToString("0.0") + " FPS");
            y += KeyValue(g, x, y, kw, "Frame-time variation", KnowledgeBase.F(_live.StdDev) + " ms");
            y += KeyValue(g, x, y, kw, "Main thread · simulation", KnowledgeBase.F(_live.Sim) + " ms");
            y += KeyValue(g, x, y, kw, "Rendering", KnowledgeBase.F(_live.Render) + " ms");
            y += KeyValue(g, x, y, kw, "Present to screen", KnowledgeBase.F(_live.Present) + " ms");
            y += 10 * S;
            SectionLabel(g, "GAPO's own overhead", x, y); y += 22 * S;
            double avg = _selfCpuN > 0 ? _selfCpuSum / _selfCpuN : m.SelfCpuPct;
            y += KeyValue(g, x, y, kw, "CPU now / average / peak", m.SelfCpuPct.ToString("0.00") + "% / " + avg.ToString("0.00") + "% / " + _monitor.SelfCpuPeakPct.ToString("0.0") + "%");
            y += KeyValue(g, x, y, kw, "Memory (private / working set)", (m.SelfPrivateBytes / 1048576.0).ToString("0") + " / " + (m.SelfWorkingSet / 1048576.0).ToString("0") + " MB");
            y += KeyValue(g, x, y, kw, "Budget", SelfCpuBudgetPct.ToString("0.0") + "% CPU · " + SelfRamBudgetMB.ToString("0") + " MB — exceeding it is treated as a bug");
            return y;
        }

        // ================================================================= autotest (CI)

        void AutotestStep(double now)
        {
            try
            {
                Directory.CreateDirectory(_autotestDir);
                if (now > 300000) { File.WriteAllText(Path.Combine(_autotestDir, "autotest-timeout.txt"), "Timed out in stage " + _autoStage); Close(); return; }
                switch (_autoStage)
                {
                    case 0:
                        if (_hw != null && _engine != null && _link.Connected && _live.Frames > 20 && _link.State == SandboxState.Running) { _autoStage = 1; _autoStageAt = now; }
                        break;
                    case 1:
                        if (now - _autoStageAt > 3500) { Shot("1-home"); StartOptimisation(); _autoStage = 2; _autoStageAt = now; }
                        break;
                    case 2:
                        if (_engine.Report != null && _engine.Report.Experiments.Count >= 3 && !_status.Measuring) { Shot("2-running"); _autoStage = 3; }
                        else if (!_engine.Running && now - _autoStageAt > 3000) _autoStage = 3;
                        break;
                    case 3:
                        if (!_engine.Running && _view == View.Results) { _autoStage = 4; _autoStageAt = now; }
                        else if (!_engine.Running && now - _autoStageAt > 5000) { _autoStage = 9; }
                        break;
                    case 4:
                        if (now - _autoStageAt > 1800)
                        {
                            Shot("3-results");
                            _details = true;
                            for (int t = 0; t < Tabs.Length; t++) { _tab = t; _scroll = 0; Shot("4-details-" + t); }
                            _tab = 0; _scroll = 0;
                            _autoStage = 9;
                        }
                        break;
                    case 9:
                        var rep = _engine != null ? _engine.Report : null;
                        File.WriteAllText(Path.Combine(_autotestDir, "report.txt"), rep != null ? rep.ToText() : "no report");
                        var m = _monitor.Latest;
                        File.WriteAllText(Path.Combine(_autotestDir, "overhead.txt"), string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "selfCpuAvgPct={0:0.000} selfCpuPeakPct={1:0.00} selfPrivateMB={2:0.0} selfWorkingSetMB={3:0.0} uiTimerAvgPct={4:0.000}",
                            rep != null ? rep.SelfCpuAvgPct : -1, _monitor.SelfCpuPeakPct, m != null ? m.SelfPrivateBytes / 1048576.0 : -1, m != null ? m.SelfWorkingSet / 1048576.0 : -1, _selfCpuN > 0 ? _selfCpuSum / _selfCpuN : -1));
                        _autoStage = 10;
                        Close();
                        break;
                }
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(_autotestDir, "autotest-error.txt"), ex.ToString()); } catch { }
                Close();
            }
        }

        void Shot(string name)
        {
            var size = ClientSize;
            using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp)) PaintAll(g, size);
                bmp.Save(Path.Combine(_autotestDir, name + ".png"), ImageFormat.Png);
            }
        }
    }
}
