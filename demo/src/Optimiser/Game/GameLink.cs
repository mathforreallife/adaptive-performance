using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Gapo.Shared;

namespace Gapo.Optimiser.Game
{
    /// <summary>
    /// Connection to the running game: reads frame telemetry from shared memory (no copies through
    /// the kernel, no sockets), edits the game's settings file, and sends benchmark-camera commands.
    /// Polls at 10 Hz — the ring buffer holds 4096 frames, so nothing is lost even at 400 FPS.
    /// </summary>
    public sealed class GameLink : IDisposable
    {
        readonly object _lock = new object();
        readonly List<FrameRecord> _frames = new List<FrameRecord>(8192);
        readonly FrameRecord[] _buffer = new FrameRecord[TelemetryChannel.Capacity];
        readonly Thread _thread;
        volatile bool _stop;
        TelemetryChannel _ch;
        long _next;
        int _pid;
        DateTime _lastBeat = DateTime.MinValue;
        const int MaxFrames = 24000;   // ~3 MB; oldest frames are dropped

        public bool Connected { get; private set; }
        public int Pid { get { return _pid; } }
        public string Title { get; private set; }
        public string SettingsPath { get; private set; }
        public string ExePath { get; private set; }
        public SandboxState State { get { var c = _ch; return c == null ? SandboxState.Exiting : c.State; } }
        public int AppliedRevision { get { var c = _ch; return c == null ? -1 : c.AppliedRevision; } }
        public int CameraMode { get { var c = _ch; return c == null ? 0 : c.CameraMode; } }
        public float CalibratedUltraMs { get { var c = _ch; return c == null ? 0 : c.CalibratedUltraMs; } }
        public int EntityCount { get { var c = _ch; return c == null ? 0 : c.EntityCount; } }
        public event Action<bool> ConnectionChanged;

        public GameLink()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "GAPO game link", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        void Loop()
        {
            while (!_stop)
            {
                try { Poll(); } catch { Disconnect(); }
                Thread.Sleep(Connected ? 100 : 1000);
            }
        }

        void Poll()
        {
            if (_ch == null)
            {
                var ch = TelemetryChannel.TryOpenForReader();
                if (ch == null) return;
                if (!IsAlive(ch.Pid) || ch.State == SandboxState.Exiting) { ch.Dispose(); return; }
                _ch = ch;
                _pid = ch.Pid;
                Title = ch.GameTitle;
                SettingsPath = ch.SettingsPath;
                try { using (var p = Process.GetProcessById(_pid)) ExePath = p.MainModule.FileName; } catch { ExePath = null; }
                _next = Math.Max(0, ch.FramesWritten - 600);
                _lastBeat = DateTime.UtcNow;
                lock (_lock) _frames.Clear();
                Connected = true;
                var h = ConnectionChanged; if (h != null) h(true);
            }
            int n = _ch.ReadNewFrames(ref _next, _buffer);
            if (n > 0)
            {
                _lastBeat = DateTime.UtcNow;
                lock (_lock)
                {
                    for (int i = 0; i < n; i++) _frames.Add(_buffer[i]);
                    if (_frames.Count > MaxFrames) _frames.RemoveRange(0, _frames.Count - MaxFrames + 2000);
                }
            }
            else if ((DateTime.UtcNow - _lastBeat).TotalSeconds > 4 && (!IsAlive(_pid) || _ch.State == SandboxState.Exiting))
                Disconnect();
        }

        static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch { return false; }
        }

        void Disconnect()
        {
            var ch = _ch;
            _ch = null;
            if (ch != null) ch.Dispose();
            if (Connected) { Connected = false; var h = ConnectionChanged; if (h != null) h(false); }
        }

        // ================================================================= frames

        public List<FrameRecord> FramesSince(double gameTimeMs)
        {
            lock (_lock)
            {
                int i = _frames.Count;
                while (i > 0 && _frames[i - 1].TimeMs >= gameTimeMs) i--;
                return _frames.GetRange(i, _frames.Count - i);
            }
        }

        public List<FrameRecord> LastFrames(int count)
        {
            lock (_lock)
            {
                int n = Math.Min(count, _frames.Count);
                return _frames.GetRange(_frames.Count - n, n);
            }
        }

        public double LatestGameTimeMs
        {
            get { lock (_lock) return _frames.Count > 0 ? _frames[_frames.Count - 1].TimeMs : 0; }
        }

        public double ObservedSeconds
        {
            get { lock (_lock) return _frames.Count > 1 ? (_frames[_frames.Count - 1].TimeMs - _frames[0].TimeMs) / 1000.0 : 0; }
        }

        // ================================================================= settings

        public SandboxSettings ReadSettings()
        {
            string path = SettingsPath;
            return string.IsNullOrEmpty(path) ? null : SandboxSettings.Load(path);
        }

        /// <summary>Writes new settings and returns the revision the game will acknowledge.</summary>
        public int WriteSettings(SandboxSettings s, string modifiedBy)
        {
            var current = ReadSettings();
            int rev = Math.Max(current == null ? 0 : current.Revision, AppliedRevision) + 1;
            var copy = s.Clone();
            copy.Revision = rev;
            copy.ModifiedBy = modifiedBy;
            copy.Save(SettingsPath);
            return rev;
        }

        /// <summary>Waits until the game reports it is running with <paramref name="rev"/>.</summary>
        public bool WaitForRevision(int rev, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var c = _ch;
                if (c == null) return false;
                if (c.AppliedRevision == rev) return true;
                Thread.Sleep(30);
            }
            return false;
        }

        // ================================================================= commands

        public bool SendCommand(SandboxCommand cmd, int arg, int timeoutMs = 2000)
        {
            var c = _ch;
            if (c == null) return false;
            c.SendCommand(cmd, arg);
            int seq = c.CommandSeq;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_ch == null) return false;
                if (c.CommandAck == seq) return true;
                Thread.Sleep(15);
            }
            return false;
        }

        public void Toast(string text)
        {
            var c = _ch;
            if (c != null) c.ShowToast(text);
        }

        public void Dispose()
        {
            _stop = true;
            try { _thread.Join(1000); } catch { }
            Disconnect();
        }
    }
}
