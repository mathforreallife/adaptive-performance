using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Gapo.Sandbox.Contract;

namespace Gapo.Sandbox
{
    public sealed unsafe class GameForm : Form
    {
        const int ExitFailed = 3, ExitClosedBeforeAutotestFinished = 5;
        bool _autotestFinished;
        readonly Game _game;
        readonly FixtureOptions _options;
        readonly InputState _input = new InputState();
        readonly double _autotestSeconds;
        readonly string _screenshotPath, _logPath;
        DxgiPresenter _presenter;
        string _presentFallbackReason;
        public int ExitCode { get; private set; }
        bool _ready, _showHud = true, _showHelp, _fullscreen;
        FormWindowState _prevState; FormBorderStyle _prevBorder; Rectangle _prevBounds;
        float _dpi = 1f;
        Point _lastMouse; bool _looking;
        Rectangle _dest;

        IntPtr _memDC, _dib, _oldBmp, _fontBig, _fontMid, _fontSmall, _fontTitle;
        int* _bits;
        int _bw, _bh;

        public GameForm(FixtureOptions options)
        {
            _options = options;
            _game = new Game(options);
            _autotestSeconds = options.AutotestSeconds;
            _screenshotPath = options.ScreenshotPath;
            _logPath = options.LogPath;
            Text = "GAPO Sandbox — test fixture game";
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            using (var g = CreateGraphics()) _dpi = g.DpiX / 96f;
            var area = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Min((int)(1280 * _dpi), (int)(area.Width * 0.86));
            int h = w * 9 / 16;
            if (h > area.Height * 0.84) { h = (int)(area.Height * 0.84); w = h * 16 / 9; }
            if (options.WindowWidth > 0) { w = options.WindowWidth; h = options.WindowHeight; }
            ClientSize = new Size(w, h);
            MinimumSize = new Size(480, 300);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.UseDarkTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                _game.Init(Loading);
            }
            catch (Exception ex)
            {
                ExitCode = ExitFailed;
                Program.ReportFailure(_options, ex.ToString());
                if (_autotestSeconds <= 0)
                    MessageBox.Show(this, "The sandbox could not start:\n\n" + ex.Message, "GAPO Sandbox", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            if (_options.PresentPath == PresentPath.Dxgi)
                _presenter = DxgiPresenter.TryCreate(Handle, Math.Max(16, ClientSize.Width), Math.Max(16, ClientSize.Height), out _presentFallbackReason);
            if (_game.LoadedOptimisedSettings)
                _game.ShowToast("Loaded settings from last session (" + _game.Settings.ModifiedBy + "). Press F5 to restore the unoptimised defaults.");
            else
                _game.ShowToast("Unoptimised defaults. Watch the FPS, then open GAPO.");
            _game.ToastUntil = _game.Now + 6500;
            _ready = true;
            _started = _game.Now;
            Application.Idle += OnIdle;
        }

        double _started;

        void Loading(string message)
        {
            using (var g = CreateGraphics())
            {
                g.Clear(Color.FromArgb(10, 14, 24));
                using (var f = new Font("Segoe UI", 16f, FontStyle.Regular))
                using (var f2 = new Font("Segoe UI", 10f))
                using (var b = new SolidBrush(Color.FromArgb(232, 238, 250)))
                using (var b2 = new SolidBrush(Color.FromArgb(140, 150, 175)))
                {
                    var sz = g.MeasureString(message, f);
                    g.DrawString(message, f, b, (ClientSize.Width - sz.Width) / 2, ClientSize.Height / 2 - sz.Height);
                    const string sub = "GAPO Sandbox · a deliberately unoptimised test game";
                    var sz2 = g.MeasureString(sub, f2);
                    g.DrawString(sub, f2, b2, (ClientSize.Width - sz2.Width) / 2, ClientSize.Height / 2 + 8);
                }
            }
            Application.DoEvents();
        }


        void OnIdle(object sender, EventArgs e)
        {
            Native.MSG msg;
            while (_ready && !IsDisposed && !Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0))
            {
                _game.Frame(_input, Present);
                if (_autotestSeconds > 0 && (_game.QuitRequested || _game.Now - _started > _autotestSeconds * 1000)) { FinishAutotest(); return; }
                if (_autotestSeconds <= 0 && _game.QuitRequested) { Close(); return; }
            }
        }

        void FinishAutotest()
        {
            _autotestFinished = true;
            _ready = false;
            Application.Idle -= OnIdle;
            try
            {
                if (!string.IsNullOrEmpty(_screenshotPath) && _dib != IntPtr.Zero)
                    using (var bmp = Image.FromHbitmap(_dib)) bmp.Save(_screenshotPath, _screenshotPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Jpeg : ImageFormat.Png);
                if (!string.IsNullOrEmpty(_logPath))
                    ResultLog.Write(_logPath, _game, _presenter != null ? "dxgi" : "gdi", _presenter != null ? _presenter.AdapterName : null,
                        _presenter != null && _presenter.IsSoftwareAdapter, _presentFallbackReason);
            }
            catch (Exception ex)
            {
                ExitCode = ExitFailed;
                Program.ReportFailure(_options, ex.ToString());
            }
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!_ready) e.Graphics.Clear(Color.FromArgb(10, 14, 24));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _ready = false;
            Application.Idle -= OnIdle;
            if (_autotestSeconds > 0 && !_autotestFinished && ExitCode == 0)
            {
                ExitCode = ExitClosedBeforeAutotestFinished;
                Program.ReportFailure(_options, "The game window was closed before the auto-test finished.");
            }
            _game.Dispose();
            if (_presenter != null) { _presenter.Dispose(); _presenter = null; }
            FreeBackBuffer();
            foreach (var f in new[] { _fontBig, _fontMid, _fontSmall, _fontTitle }) if (f != IntPtr.Zero) Native.DeleteObject(f);
            base.OnFormClosed(e);
        }


        void EnsureBackBuffer(IntPtr hdc, int w, int h)
        {
            if (_memDC != IntPtr.Zero && w == _bw && h == _bh) return;
            FreeBackBuffer();
            _memDC = Native.CreateCompatibleDC(hdc);
            var bmi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            _dib = Native.CreateDIBSection(hdc, ref bmi, 0, out bits, IntPtr.Zero, 0);
            _bits = (int*)bits;
            _oldBmp = Native.SelectObject(_memDC, _dib);
            _bw = w; _bh = h;
            if (_fontBig == IntPtr.Zero)
            {
                _fontBig = Native.CreateFontW(-(int)(34 * _dpi), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                _fontMid = Native.CreateFontW(-(int)(15 * _dpi), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                _fontSmall = Native.CreateFontW(-(int)(12.5f * _dpi), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                _fontTitle = Native.CreateFontW(-(int)(11.5f * _dpi), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            }
        }

        void FreeBackBuffer()
        {
            if (_memDC == IntPtr.Zero) return;
            Native.SelectObject(_memDC, _oldBmp);
            Native.DeleteObject(_dib);
            Native.DeleteDC(_memDC);
            _memDC = IntPtr.Zero; _dib = IntPtr.Zero; _bits = null;
        }

        float Present()
        {
            double t0 = _game.Now;
            int cw = ClientSize.Width, ch = ClientSize.Height;
            if (cw < 16 || ch < 16 || WindowState == FormWindowState.Minimized) return 0;
            IntPtr hdc = Native.GetDC(Handle);
            try
            {
                EnsureBackBuffer(hdc, cw, ch);
                var r = _game.Renderer;
                int dw = cw, dh = cw * r.OutH / r.OutW;
                if (dh > ch) { dh = ch; dw = ch * r.OutW / r.OutH; }
                _dest = new Rectangle((cw - dw) / 2, (ch - dh) / 2, dw, dh);
                if (dw < cw || dh < ch) for (int i = 0; i < cw * ch; i++) _bits[i] = 0;
                var bmi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = r.OutW, biHeight = -r.OutH, biPlanes = 1, biBitCount = 32 };
                Native.SetStretchBltMode(_memDC, Native.COLORONCOLOR);
                fixed (int* src = r.Output)
                    Native.StretchDIBits(_memDC, _dest.X, _dest.Y, dw, dh, 0, 0, r.OutW, r.OutH, (IntPtr)src, ref bmi, 0, Native.SRCCOPY);
                DrawOverlay(cw, ch);
                if (_presenter != null)
                {
                    _presenter.Present(_bits, cw, ch, _game.GpuLoadIterations);
                    _game.PresentWaitMs = _presenter.LastPresentCallMs;
                }
                else Native.BitBlt(hdc, 0, 0, cw, ch, _memDC, 0, 0, Native.SRCCOPY);
            }
            finally { Native.ReleaseDC(Handle, hdc); }
            return (float)(_game.Now - t0);
        }


        int S(float v) { return (int)(v * _dpi); }

        void Darken(int x, int y, int w, int h)
        {
            int x1 = Math.Min(_bw, x + w), y1 = Math.Min(_bh, y + h);
            x = Math.Max(0, x); y = Math.Max(0, y);
            for (int yy = y; yy < y1; yy++)
            {
                int* p = _bits + yy * _bw + x;
                for (int xx = x; xx < x1; xx++, p++) *p = ((*p >> 2) & 0x3F3F3F) + 0x0B0E16;
            }
        }

        void FillPx(int x, int y, int w, int h, int rgb)
        {
            int x1 = Math.Min(_bw, x + w), y1 = Math.Min(_bh, y + h);
            x = Math.Max(0, x); y = Math.Max(0, y);
            for (int yy = y; yy < y1; yy++)
            {
                int* p = _bits + yy * _bw + x;
                for (int xx = x; xx < x1; xx++) *p++ = rgb;
            }
        }

        void Label(IntPtr font, int x, int y, string s, int r, int g, int b)
        {
            Native.SelectObject(_memDC, font);
            Native.SetTextColor(_memDC, Native.Rgb(r, g, b));
            Native.TextOutW(_memDC, x, y, s, s.Length);
        }

        void DrawOverlay(int cw, int ch)
        {
            Native.SetBkMode(_memDC, Native.TRANSPARENT);
            var last = _game.Last;
            if (_showHud)
            {
                int px = S(14), py = S(14), pw = S(316), ph = S(186);
                Darken(px, py, pw, ph);
                FillPx(px, py, S(3), ph, 0x22D3EE);
                int x = px + S(16);
                Label(_fontTitle, x, py + S(10), "GAPO SANDBOX", 150, 170, 205);
                var s = _game.Settings;
                bool gapo = s.ModifiedBy.IndexOf("GAPO", StringComparison.OrdinalIgnoreCase) >= 0;
                bool defaults = s.IsUnoptimisedDefaults();
                string badge = defaults ? "UNOPTIMISED" : (gapo ? "OPTIMISED BY GAPO" : "CUSTOM SETTINGS");
                if (defaults) Label(_fontTitle, x + S(116), py + S(10), badge, 248, 113, 113);
                else if (gapo) Label(_fontTitle, x + S(116), py + S(10), badge, 52, 211, 153);
                else Label(_fontTitle, x + S(116), py + S(10), badge, 251, 191, 36);

                string fps = _game.Fps > 0 ? _game.Fps.ToString("0") : "--";
                int fr, fg, fb;
                if (_game.Fps >= 55) { fr = 52; fg = 211; fb = 153; } else if (_game.Fps >= 38) { fr = 251; fg = 191; fb = 36; } else { fr = 248; fg = 113; fb = 113; }
                Label(_fontBig, x, py + S(26), fps, fr, fg, fb);
                Label(_fontMid, x + S(18 * fps.Length + 10), py + S(44), "FPS", 200, 210, 230);
                Label(_fontMid, x + S(150), py + S(34), (_game.Fps > 0 ? (1000f / _game.Fps).ToString("0.0") : "--") + " ms", 232, 238, 250);
                Label(_fontSmall, x + S(150), py + S(54), "1% low " + (_game.OnePercentLow > 0 ? _game.OnePercentLow.ToString("0") : "--") + " FPS", 150, 165, 195);

                int gx = x, gy = py + S(82), gw = S(284), gh = S(52);
                FillPx(gx, gy, gw, gh, 0x0E1320);
                int line16 = gy + gh - (int)(gh * 16.7f / 60f), line33 = gy + gh - (int)(gh * 33.3f / 60f);
                FillPx(gx, line16, gw, 1, 0x1F3A2E); FillPx(gx, line33, gw, 1, 0x3A2F1B);
                int bars = gw / Math.Max(1, S(2));
                int bw = Math.Max(1, S(2));
                var hist = _game.History;
                for (int i = 0; i < bars; i++)
                {
                    int idx = (_game.HistoryPos - bars + i + hist.Length * 4) % hist.Length;
                    float v = hist[idx];
                    if (v <= 0) continue;
                    int bh = Math.Min(gh, Math.Max(1, (int)(gh * v / 60f)));
                    int col = v <= 20f ? 0x34D399 : (v <= 40f ? 0xFBBF24 : 0xF87171);
                    FillPx(gx + i * bw, gy + gh - bh, Math.Max(1, bw - (bw > 1 ? 1 : 0)), bh, col);
                }
                Label(_fontSmall, gx + gw - S(44), gy + S(1), "60 ms", 90, 100, 125);

                string res = last.RenderWidth + "×" + last.RenderHeight + (s.AntiAliasing == AntiAliasing.SSAA4x ? " (4× SSAA)" : "");
                Label(_fontSmall, x, py + S(140), "Render " + res + " · " + last.RenderThreads + (last.RenderThreads == 1 ? " thread" : " threads") +
                    " · " + last.EntitiesSimulated + "/" + last.EntitiesTotal + " sims", 150, 165, 195);
                string cam = _game.Mode == CameraMode.Benchmark ? "Benchmark flight (GAPO is measuring)" : (_game.Mode == CameraMode.Manual ? "Manual" : "Autopilot");
                Label(_fontSmall, x, py + S(158), "Camera: " + cam + " · F1 help", 150, 165, 195);
            }

            if (_game.World.ChunksRemaining > 0)
            {
                string w = "Streaming world… " + (100 - _game.World.ChunksRemaining * 100 / _game.World.ChunkReady.Length) + "%";
                Darken(cw - S(250), S(14), S(236), S(30));
                Label(_fontSmall, cw - S(238), S(20), w, 251, 191, 36);
            }

            if (_game.Toast != null && _game.Now < _game.ToastUntil)
            {
                string t = _game.Toast;
                int tw = Math.Min(cw - S(40), S(10 + 7.2f * t.Length)), th = S(40);
                int tx = (cw - tw) / 2, ty = ch - th - S(26);
                Darken(tx, ty, tw, th);
                FillPx(tx, ty, tw, S(2), 0x8B5CF6);
                var rc = new Native.RECT { Left = tx + S(10), Top = ty, Right = tx + tw - S(10), Bottom = ty + th };
                Native.SelectObject(_memDC, _fontSmall);
                Native.SetTextColor(_memDC, Native.Rgb(236, 240, 250));
                Native.DrawTextW(_memDC, t, t.Length, ref rc, Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
            }

            if (_showHelp)
            {
                int hw = S(440), hh = S(292), hx = (cw - hw) / 2, hy = (ch - hh) / 2;
                Darken(hx, hy, hw, hh); Darken(hx, hy, hw, hh);
                FillPx(hx, hy, hw, S(2), 0x22D3EE);
                string[] lines =
                {
                    "W A S D — fly      ← → / Q E — turn      R / F — up / down",
                    "Shift — fly faster      Middle-drag — look around",
                    "Left mouse — raise terrain      Right mouse — dig",
                    "G — spawn a herd of creatures",
                    "F3 — hide/show HUD      F11 — fullscreen",
                    "F5 — reset to the unoptimised defaults",
                    "F6 — re-calibrate the workload to this PC (on next start)",
                    "",
                    "Leave the keys alone for 12 s and the autopilot takes over.",
                    "Settings live in %LOCALAPPDATA%\\GAPO-Sandbox\\sandbox-settings.ini",
                };
                Label(_fontMid, hx + S(18), hy + S(14), "Controls", 232, 238, 250);
                for (int i = 0; i < lines.Length; i++) Label(_fontSmall, hx + S(18), hy + S(46) + i * S(22), lines[i], 190, 200, 220);
            }
        }


        protected override void OnKeyDown(KeyEventArgs e)
        {
            SetKey(e.KeyCode, true);
            switch (e.KeyCode)
            {
                case Keys.F1: _showHelp = !_showHelp; break;
                case Keys.F3: _showHud = !_showHud; break;
                case Keys.F5: if (_ready) _game.ResetToUnoptimisedDefaults(); break;
                case Keys.F6: _game.DeleteCalibration(); _game.ShowToast("Calibration cleared — it will be redone the next time the game starts"); break;
                case Keys.F11: ToggleFullscreen(); break;
                case Keys.Escape: if (_fullscreen) ToggleFullscreen(); else if (_showHelp) _showHelp = false; break;
                case Keys.G: _input.SpawnHerd = true; break;
            }
            e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e) { SetKey(e.KeyCode, false); }

        void SetKey(Keys k, bool down)
        {
            switch (k)
            {
                case Keys.W: case Keys.Up: _input.Forward = down; break;
                case Keys.S: case Keys.Down: _input.Back = down; break;
                case Keys.A: _input.Left = down; break;
                case Keys.D: _input.Right = down; break;
                case Keys.Q: case Keys.Left: _input.TurnLeft = down; break;
                case Keys.E: case Keys.Right: _input.TurnRight = down; break;
                case Keys.R: case Keys.Space: _input.Up = down; break;
                case Keys.F: case Keys.C: _input.Down = down; break;
                case Keys.ShiftKey: _input.Fast = down; break;
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            _input.Forward = _input.Back = _input.Left = _input.Right = _input.TurnLeft = _input.TurnRight = _input.Up = _input.Down = _input.Fast = false;
            _input.EditDir = 0; _looking = false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Middle) { _looking = true; _lastMouse = e.Location; return; }
            _input.EditDir = e.Button == MouseButtons.Left ? 1 : (e.Button == MouseButtons.Right ? -1 : 0);
            MapMouse(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_looking)
            {
                _input.MouseTurn += (e.X - _lastMouse.X) * 0.005f;
                _lastMouse = e.Location;
            }
            if (_input.EditDir != 0) MapMouse(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Middle) _looking = false;
            else _input.EditDir = 0;
        }

        void MapMouse(Point p)
        {
            if (_dest.Width <= 0 || _game.Renderer == null) return;
            _input.EditX = (p.X - _dest.X) * _game.Renderer.OutW / _dest.Width;
            _input.EditY = (p.Y - _dest.Y) * _game.Renderer.OutH / _dest.Height;
        }

        void ToggleFullscreen()
        {
            if (!_fullscreen)
            {
                _prevState = WindowState; _prevBorder = FormBorderStyle; _prevBounds = Bounds;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = Screen.FromControl(this).Bounds;
                _fullscreen = true;
            }
            else
            {
                FormBorderStyle = _prevBorder;
                Bounds = _prevBounds;
                WindowState = _prevState;
                _fullscreen = false;
            }
        }
    }
}
