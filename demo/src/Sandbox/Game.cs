using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Gapo.Shared;

namespace Gapo.Sandbox
{
    public enum CameraMode { Autopilot = 1, Manual = 0, Benchmark = 2 }

    public sealed class InputState
    {
        public bool Forward, Back, Left, Right, TurnLeft, TurnRight, Up, Down, Fast;
        public float MouseTurn;           // accumulated yaw from mouse drag (radians)
        public int EditX = -1, EditY = -1; // output-pixel coordinates of a terrain edit
        public int EditDir;               // +1 raise, -1 lower
        public bool SpawnHerd;
        public bool AnyMovement { get { return Forward || Back || Left || Right || TurnLeft || TurnRight || Up || Down || MouseTurn != 0; } }
    }

    /// <summary>The game loop, independent of how frames are shown (window or headless).</summary>
    public sealed class Game : IDisposable
    {
        public World World;
        public Renderer Renderer;
        public Simulation Sim;
        public TelemetryChannel Telemetry;
        public SandboxSettings Settings = SandboxSettings.UnoptimisedDefaults();
        public int BaseW = 480, BaseH = 270, EntityBase = 500;
        public float CalibratedMs;
        public float Detail = 1f;
        public bool LoadedOptimisedSettings;

        public CameraMode Mode = CameraMode.Autopilot;
        public float CamX, CamY, CamH, Yaw;
        double _autoT = 0, _benchT = 0, _lastInputTime = -100;
        public double Now { get { return _clock.Elapsed.TotalMilliseconds; } }

        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly List<Renderer.Sprite> _sprites = new List<Renderer.Sprite>(4096);
        readonly string _settingsPath = DemoPaths.SettingsFile;
        DateTime _settingsStamp;
        double _nextSettingsCheck;
        double _nextAutosave;
        double _lastFrameEnd;
        long _frameIndex;
        int _cmdSeq, _toastSeq;
        int _lastGen2;
        double _nextChunkBake;

        // HUD data
        public readonly float[] History = new float[240];
        public int HistoryPos;
        public FrameRecord Last;
        public string Toast;
        public double ToastUntil;
        public float Fps, OnePercentLow;
        double _statsAt;

        public void Init(Action<string> progress)
        {
            progress("Generating world…");
            World = new World(20260930);
            Renderer = new Renderer(World);
            Sim = new Simulation(World);

            progress("Calibrating the demo to your PC…");
            if (!LoadCalibration()) Calibrate();

            Sim.Populate(EntityBase);
            Renderer.Detail = Detail;
            LoadOrCreateSettings();
            ApplySettings(Settings, false);

            Telemetry = TelemetryChannel.CreateForWriter();
            Telemetry.Pid = Process.GetCurrentProcess().Id;
            Telemetry.GameTitle = "GAPO Sandbox";
            Telemetry.SettingsPath = _settingsPath;
            Telemetry.ContentLevel = BaseW;
            Telemetry.CalibratedUltraMs = CalibratedMs;
            Telemetry.EntityCount = Sim.Total;
            Telemetry.AppliedRevision = Settings.Revision;
            Telemetry.State = SandboxState.Warmup;

            float x, y, yaw;
            CameraPath.Position(0, out x, out y, out yaw);
            CamX = x; CamY = y; Yaw = yaw; CamH = CameraPath.Height(World, 0);
            World.QueueChunksAround(CamX, CamY);
            _nextAutosave = Now + Math.Max(1, Settings.AutosaveSeconds) * 1000.0;
            _lastFrameEnd = Now;
            _lastGen2 = GC.CollectionCount(2);
        }

        // ================================================================= calibration

        /// <summary>
        /// Scales the world so the unoptimised "Ultra" defaults land at roughly 35 FPS on this PC:
        /// laggy enough to notice, never a slideshow — whatever hardware the demo runs on.
        /// </summary>
        void Calibrate()
        {
            World.BakeAllChunks();     // colours don't change the cost; avoid measuring the warm-up here
            var ultra = SandboxSettings.UnoptimisedDefaults();
            Renderer.Settings = ultra; Renderer.Threads = 1;
            float x, y, yaw;
            CameraPath.Position(CameraPath.BenchmarkStart + 1, out x, out y, out yaw);
            Renderer.CamX = x; Renderer.CamY = y; Renderer.Yaw = yaw; Renderer.CamH = CameraPath.Height(World, CameraPath.BenchmarkStart + 1);

            const int probeW = 320;
            Renderer.Resize(probeW, probeW * 9 / 16);
            var times = new List<double>();
            for (int i = 0; i < 7; i++)
            {
                var sw = Stopwatch.StartNew();
                Renderer.Render(_sprites, Sim.PX, Sim.PY, Sim.PZ, 0);
                if (i >= 2) times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double renderProbe = times[times.Count / 2];

            // Target ~17 ms of rendering; cost grows roughly with width^1.9. On PCs too slow even at the
            // smallest size, fewer ray-march steps ("Detail") take the rest of the load off.
            double w = probeW * Math.Pow(17.0 / Math.Max(0.5, renderProbe), 1 / 1.9);
            BaseW = Clamp((int)(w / 32) * 32, 224, 800);
            BaseH = (BaseW * 9 / 16) & ~1;
            Detail = 1f;
            for (int pass = 0; pass < 3; pass++)
            {
                Renderer.Detail = Detail;
                Renderer.Resize(BaseW, BaseH);
                var check = new List<double>();
                for (int i = 0; i < 5; i++)
                {
                    var sw = Stopwatch.StartNew();
                    Renderer.Render(_sprites, Sim.PX, Sim.PY, Sim.PZ, 0);
                    if (i >= 1) check.Add(sw.Elapsed.TotalMilliseconds);
                }
                check.Sort();
                double m = check[check.Count / 2];
                renderProbe = m;
                if (m <= 17.0 * 1.2 || Detail <= 0.45f) break;
                Detail = Math.Max(0.45f, Detail * (float)(17.0 / m) * 1.05f);
            }

            // Simulation probe: 300 creatures, all simulated, 8 physics substeps.
            var probeSim = new Simulation(World);
            probeSim.Populate(300);
            var st = new List<double>();
            for (int i = 0; i < 6; i++)
            {
                var sw = Stopwatch.StartNew();
                probeSim.Update(0.016f, ultra, x, y, Renderer.CamH);
                if (i >= 1) st.Add(probeSim.EntitiesMs + probeSim.PhysicsMs);
            }
            st.Sort();
            double simProbe = st[st.Count / 2];
            EntityBase = Clamp((int)(300 * Math.Sqrt(7.5 / Math.Max(0.2, simProbe))), 80, 1400);
            CalibratedMs = (float)(renderProbe + simProbe * Math.Pow(EntityBase / 300.0, 2));
            SaveCalibration();

            // Start the real game with the streamed warm-up.
            for (int i = 0; i < World.ChunkReady.Length; i++) World.ChunkReady[i] = false;
            World.ChunksRemaining = World.ChunkReady.Length;
        }

        bool LoadCalibration()
        {
            try
            {
                if (!File.Exists(DemoPaths.CalibrationFile)) return false;
                var map = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(DemoPaths.CalibrationFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                string ver, machine;
                if (!map.TryGetValue("Version", out ver) || ver != "4") return false;
                if (!map.TryGetValue("Machine", out machine) || machine != MachineKey()) return false;
                BaseW = Clamp(int.Parse(map["BaseWidth"], CultureInfo.InvariantCulture), 224, 800);
                BaseH = (BaseW * 9 / 16) & ~1;
                EntityBase = Clamp(int.Parse(map["Entities"], CultureInfo.InvariantCulture), 120, 1400);
                CalibratedMs = float.Parse(map["UltraMs"], CultureInfo.InvariantCulture);
                Detail = Math.Max(0.45f, Math.Min(1f, float.Parse(map["Detail"], CultureInfo.InvariantCulture)));
                return true;
            }
            catch { return false; }
        }

        void SaveCalibration()
        {
            try
            {
                Directory.CreateDirectory(DemoPaths.DataDir);
                File.WriteAllText(DemoPaths.CalibrationFile,
                    "; Delete this file (or press F6 in the game) to re-calibrate the demo's workload to this PC.\r\n" +
                    "Version=4\r\nMachine=" + MachineKey() + "\r\nBaseWidth=" + BaseW.ToString(CultureInfo.InvariantCulture) +
                    "\r\nEntities=" + EntityBase.ToString(CultureInfo.InvariantCulture) +
                    "\r\nUltraMs=" + CalibratedMs.ToString("0.0", CultureInfo.InvariantCulture) +
                    "\r\nDetail=" + Detail.ToString("0.00", CultureInfo.InvariantCulture) + "\r\n");
            }
            catch { }
        }

        public static void DeleteCalibration()
        {
            try { File.Delete(DemoPaths.CalibrationFile); } catch { }
        }

        static string MachineKey() { return Environment.MachineName + "/" + Environment.ProcessorCount; }

        // ================================================================= settings

        void LoadOrCreateSettings()
        {
            var s = SandboxSettings.Load(_settingsPath);
            if (s == null)
            {
                s = SandboxSettings.UnoptimisedDefaults();
                try { s.Save(_settingsPath); } catch { }
            }
            LoadedOptimisedSettings = !s.IsUnoptimisedDefaults();
            Settings = s;
            try { _settingsStamp = File.GetLastWriteTimeUtc(_settingsPath); } catch { }
        }

        public void ResetToUnoptimisedDefaults()
        {
            var s = SandboxSettings.UnoptimisedDefaults();
            s.Revision = Settings.Revision + 1;
            s.ModifiedBy = "Player (F5 reset)";
            try { s.Save(_settingsPath); } catch { }
            ShowToast("Settings reset to the unoptimised defaults");
        }

        void ApplySettings(SandboxSettings s, bool announce)
        {
            var old = Settings;
            Settings = s;
            Renderer.Settings = s;
            Renderer.Threads = s.RenderThreads;
            World.SetTextureQuality(s.Textures);
            ResizeRender();
            if (Telemetry != null) Telemetry.AppliedRevision = s.Revision;
            if (s.AutosaveSeconds != old.AutosaveSeconds) _nextAutosave = Now + Math.Max(1, s.AutosaveSeconds) * 1000.0;
            if (announce && s.ModifiedBy.IndexOf("GAPO", StringComparison.OrdinalIgnoreCase) < 0)
                ShowToast("Settings reloaded (" + s.ModifiedBy + ")");
        }

        void ResizeRender()
        {
            int w = Math.Max(64, (BaseW * Settings.ResolutionScale / 100) & ~1);
            int h = Math.Max(36, (BaseH * Settings.ResolutionScale / 100) & ~1);
            Renderer.Resize(w, h);
        }

        bool CheckSettingsFile()
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(_settingsPath); } catch { return false; }
            if (stamp == _settingsStamp) return false;
            var s = SandboxSettings.Load(_settingsPath);
            if (s == null) return false;
            _settingsStamp = stamp;
            ApplySettings(s, true);
            return true;
        }

        public void ShowToast(string text)
        {
            Toast = text;
            ToastUntil = Now + 3200;
        }

        // ================================================================= frame

        public FrameRecord Frame(InputState input, Func<float> present)
        {
            double frameStart = Now;
            int flags = 0;
            float ioMs = 0;

            if (frameStart >= _nextSettingsCheck)
            {
                _nextSettingsCheck = frameStart + 200;
                if (CheckSettingsFile()) flags |= (int)FrameFlags.SettingsReload;
            }

            SandboxCommand cmd; int arg; string toast;
            while (Telemetry.TryTakeCommand(ref _cmdSeq, out cmd, out arg))
            {
                if (cmd == SandboxCommand.BenchmarkCamera)
                {
                    if (arg != 0) { Mode = CameraMode.Benchmark; _benchT = 0; }
                    else { Mode = CameraMode.Autopilot; _autoT = CameraPath.BenchmarkStart + _benchT; }
                }
                else if (cmd == SandboxCommand.RestartBenchmarkPath) _benchT = 0;
            }
            if (Telemetry.TryTakeToast(ref _toastSeq, out toast)) ShowToast(toast);

            float dt = (float)Math.Min(0.05, Math.Max(0.001, (frameStart - (_lastFrameStart > 0 ? _lastFrameStart : frameStart - 16)) / 1000.0));
            _lastFrameStart = frameStart;
            UpdateCamera(input, dt);
            if (input.EditDir != 0 && input.EditX >= 0) { EditTerrain(input); flags |= (int)FrameFlags.TerrainEdit; }
            if (input.SpawnHerd)
            {
                input.SpawnHerd = false;
                float fx = CamX + (float)Math.Cos(Yaw) * 90f, fy = CamY + (float)Math.Sin(Yaw) * 90f;
                Sim.SpawnHerd(fx, fy, 25);
                Telemetry.EntityCount = Sim.Total;
                ShowToast("25 more creatures spawned (" + Sim.Total + " in the world)");
            }

            // Simulation (main thread).
            Sim.Update(dt, Settings, CamX, CamY, CamH);
            float simMs = Sim.EntitiesMs + Sim.PhysicsMs + Sim.ParticlesMs;

            // Streaming: world chunks still being prepared right after start-up.
            if (World.ChunksRemaining > 0)
            {
                var sw = Stopwatch.StartNew();
                if (frameStart >= _nextChunkBake) { World.BakeChunks(40); _nextChunkBake = frameStart + 110; }
                ioMs += (float)sw.Elapsed.TotalMilliseconds;
                flags |= (int)FrameFlags.Warmup;
                if (World.ChunksRemaining == 0) Telemetry.State = SandboxState.Running;
            }
            else if (Telemetry.State != SandboxState.Running) Telemetry.State = SandboxState.Running;

            World.SunElevationDeg = 34f + 10f * (float)Math.Sin(Now / 1000.0 * 2 * Math.PI / 240.0);
            if (Settings.Shadows == ShadowQuality.High) World.UpdateBakedShadows(24);

            // Blocking autosave: the periodic hitch.
            if (Settings.AutosaveSeconds > 0 && frameStart >= _nextAutosave)
            {
                var sw = Stopwatch.StartNew();
                Autosave();
                ioMs += (float)sw.Elapsed.TotalMilliseconds;
                flags |= (int)FrameFlags.Autosave;
                _nextAutosave = Now + Settings.AutosaveSeconds * 1000.0;
            }

            // Render.
            Renderer.CamX = CamX; Renderer.CamY = CamY; Renderer.CamH = CamH; Renderer.Yaw = Yaw;
            Renderer.TimeSec = (float)(Now / 1000.0);
            Sim.CollectSprites(_sprites, Yaw);
            Renderer.Render(_sprites, Sim.PX, Sim.PY, Sim.PZ, Sim.ParticleCount);
            float renderMs = Renderer.TerrainMs + Renderer.SkyMs + Renderer.ShadowMs + Renderer.WaterMs + Renderer.FogMs + Renderer.SpritesMs + Renderer.PostMs;

            float presentMs = present != null ? present() : 0f;
            double workEnd = Now;
            float workMs = (float)(workEnd - frameStart);

            // Frame cap / VSync.
            float waitMs = 0;
            if (Settings.FrameRateCap > 0)
            {
                double target = _lastFrameEnd + 1000.0 / Settings.FrameRateCap;
                if (target < workEnd - 50) target = workEnd;    // don't try to catch up after a hitch
                WaitUntil(target);
                waitMs = (float)(Now - workEnd);
            }
            else if (Settings.VSync && Native.IsWindows)
            {
                try { Native.DwmFlush(); } catch { }
                waitMs = (float)(Now - workEnd);
            }

            int gen2 = GC.CollectionCount(2);
            if (gen2 != _lastGen2) { flags |= (int)FrameFlags.GarbageCollect; _lastGen2 = gen2; }
            if (Mode == CameraMode.Manual) flags |= (int)FrameFlags.ManualCamera;
            if (Mode == CameraMode.Benchmark) flags |= (int)FrameFlags.BenchmarkCamera;

            double frameEnd = Now;
            var rec = new FrameRecord
            {
                FrameIndex = _frameIndex++,
                TimeMs = frameEnd,
                FrameMs = (float)(frameEnd - _lastFrameEnd),
                WorkMs = workMs,
                SimMs = simMs,
                SimEntitiesMs = Sim.EntitiesMs,
                SimPhysicsMs = Sim.PhysicsMs,
                SimParticlesMs = Sim.ParticlesMs,
                RenderMs = renderMs,
                TerrainMs = Renderer.TerrainMs,
                ShadowMs = Renderer.ShadowMs,
                WaterMs = Renderer.WaterMs,
                SkyMs = Renderer.SkyMs,
                FogMs = Renderer.FogMs,
                SpritesMs = Renderer.SpritesMs,
                PostMs = Renderer.PostMs,
                PresentMs = presentMs,
                IoMs = ioMs,
                WaitMs = waitMs,
                Revision = Settings.Revision,
                Flags = flags,
                EntitiesSimulated = Sim.ActiveCount,
                EntitiesTotal = Sim.Considered,
                ParticlesActive = Sim.ParticleCount,
                RenderThreads = Renderer.Threads,
                RenderWidth = Renderer.OutW,
                RenderHeight = Renderer.OutH,
            };
            _lastFrameEnd = frameEnd;
            Telemetry.WriteFrame(ref rec);
            Telemetry.CameraMode = (int)Mode;
            Last = rec;
            History[HistoryPos] = rec.FrameMs;
            HistoryPos = (HistoryPos + 1) % History.Length;
            if (frameEnd - _statsAt > 500) { _statsAt = frameEnd; UpdateStats(); }
            return rec;
        }

        double _lastFrameStart;

        void UpdateStats()
        {
            var tmp = new List<float>(History.Length);
            float sum = 0;
            foreach (var f in History) if (f > 0) { tmp.Add(f); sum += f; }
            if (tmp.Count == 0) return;
            Fps = 1000f * tmp.Count / sum;
            tmp.Sort();
            int n = Math.Max(1, tmp.Count / 100);
            float worst = 0;
            for (int i = tmp.Count - n; i < tmp.Count; i++) worst += tmp[i];
            OnePercentLow = 1000f * n / worst;
        }

        /// <summary>Precise frame-cap wait: coarse 1 ms sleeps, then a short spin for the last ~1.5 ms.</summary>
        void WaitUntil(double targetMs)
        {
            while (targetMs - Now > 1.6) Thread.Sleep(1);
            while (Now < targetMs) Thread.SpinWait(40);
        }

        // ================================================================= camera

        void UpdateCamera(InputState input, float dt)
        {
            if (input.AnyMovement)
            {
                _lastInputTime = Now;
                if (Mode == CameraMode.Autopilot) Mode = CameraMode.Manual;
            }
            if (Mode == CameraMode.Manual && Now - _lastInputTime > 12000)
            {
                Mode = CameraMode.Autopilot;
                ShowToast("Autopilot resumed");
            }

            if (Mode == CameraMode.Autopilot)
            {
                _autoT += dt;
                FollowPath((float)(_autoT % CameraPath.LoopSeconds), dt);
            }
            else if (Mode == CameraMode.Benchmark)
            {
                _benchT += dt;
                float t = CameraPath.BenchmarkStart + (float)(_benchT % 12.0);
                float x, y, yaw;
                CameraPath.Position(t, out x, out y, out yaw);
                CamX = x; CamY = y; Yaw = yaw; CamH = CameraPath.Height(World, t);
            }
            else
            {
                float speed = input.Fast ? 150f : 70f;
                Yaw += ((input.TurnRight ? 1 : 0) - (input.TurnLeft ? 1 : 0)) * 1.6f * dt + input.MouseTurn;
                input.MouseTurn = 0;
                float fx = (float)Math.Cos(Yaw), fy = (float)Math.Sin(Yaw);
                float mf = (input.Forward ? 1 : 0) - (input.Back ? 1 : 0), ms = (input.Right ? 1 : 0) - (input.Left ? 1 : 0);
                CamX += (fx * mf - fy * ms) * speed * dt;
                CamY += (fy * mf + fx * ms) * speed * dt;
                CamH += ((input.Up ? 1 : 0) - (input.Down ? 1 : 0)) * 60f * dt;
                float ground = World.SurfaceAt(CamX, CamY);
                if (CamH < ground + 10f) CamH = ground + 10f;
                if (CamH > 420f) CamH = 420f;
            }
            CamX = ((CamX % World.Size) + World.Size) % World.Size;
            CamY = ((CamY % World.Size) + World.Size) % World.Size;
        }

        void FollowPath(float t, float dt)
        {
            float x, y, yaw;
            CameraPath.Position(t, out x, out y, out yaw);
            float h = CameraPath.Height(World, t);
            // Glide back onto the path smoothly after manual control.
            float k = Math.Min(1f, dt * 1.5f);
            float dx = x - CamX, dy = y - CamY;
            dx -= (float)Math.Round(dx / World.Size) * World.Size;
            dy -= (float)Math.Round(dy / World.Size) * World.Size;
            if (dx * dx + dy * dy > 4) { CamX += dx * k; CamY += dy * k; }
            else { CamX = x; CamY = y; }
            float dyaw = yaw - Yaw;
            while (dyaw > Math.PI) dyaw -= (float)(2 * Math.PI);
            while (dyaw < -Math.PI) dyaw += (float)(2 * Math.PI);
            Yaw += Math.Abs(dyaw) > 0.01f ? dyaw * k : dyaw;
            CamH += (h - CamH) * Math.Min(1f, dt * 2.5f);
        }

        void EditTerrain(InputState input)
        {
            int cell = Renderer.PickCell(input.EditX, input.EditY);
            if (cell < 0) return;
            int cx = cell & World.Mask, cy = cell >> World.Shift;
            const int R = 16;
            float amount = input.EditDir * 3.5f;
            for (int y = -R; y <= R; y++)
                for (int x = -R; x <= R; x++)
                {
                    float d = (float)Math.Sqrt(x * x + y * y) / R;
                    if (d >= 1) continue;
                    int i = World.Index(cx + x, cy + y);
                    float f = (1 - d * d); f *= f;
                    World.Height[i] = Math.Max(4f, Math.Min(300f, World.Height[i] + amount * f));
                }
            World.RebakeArea(cx, cy, R + 3);
        }

        // ================================================================= autosave

        void Autosave()
        {
            try
            {
                Directory.CreateDirectory(DemoPaths.DataDir);
                var hm = World.Height;
                var bytes = new byte[hm.Length];                       // a fresh 4 MB buffer every save
                for (int i = 0; i < hm.Length; i++) bytes[i] = (byte)Math.Min(255f, hm[i]);
                var entities = Sim.SerializeEntitiesNaively();
                uint crc = Crc32(bytes) ^ Crc32(entities);
                using (var fs = new FileStream(DemoPaths.SaveFile, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(BitConverter.GetBytes(crc), 0, 4);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Write(entities, 0, entities.Length);
                    fs.Flush(true);
                }
            }
            catch { }
        }

        static uint[] _crcTable;
        static uint Crc32(byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[i] = c;
                }
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++) crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public void Dispose()
        {
            if (Telemetry != null) { Telemetry.State = SandboxState.Exiting; Telemetry.Dispose(); Telemetry = null; }
        }
    }
}
