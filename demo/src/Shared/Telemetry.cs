using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace Gapo.Shared
{
    [Flags]
    public enum FrameFlags
    {
        None = 0,
        Warmup = 1,          // world chunks / "shaders" still being prepared
        Autosave = 2,        // a blocking save happened inside this frame
        GarbageCollect = 4,  // a gen-2 garbage collection happened inside this frame
        SettingsReload = 8,  // new settings were applied at the start of this frame
        ManualCamera = 16,
        BenchmarkCamera = 32,
        TerrainEdit = 64,
        Calibrating = 128,
    }

    /// <summary>One frame of engine telemetry, as a profiler hook in a real game would report it.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct FrameRecord
    {
        public long FrameIndex;
        public double TimeMs;          // since the game started
        public float FrameMs;          // present-to-present interval (what the player feels)
        public float WorkMs;           // busy time: everything except waiting for the frame cap
        public float SimMs;            // main thread: simulation total
        public float SimEntitiesMs;
        public float SimPhysicsMs;
        public float SimParticlesMs;
        public float RenderMs;         // rendering total (all passes)
        public float TerrainMs;
        public float ShadowMs;
        public float WaterMs;
        public float SkyMs;
        public float FogMs;
        public float SpritesMs;
        public float PostMs;
        public float PresentMs;
        public float IoMs;
        public float WaitMs;
        public int Revision;           // settings revision this frame was produced with
        public int Flags;
        public int EntitiesSimulated;
        public int EntitiesTotal;
        public int ParticlesActive;
        public int RenderThreads;
        public int RenderWidth;
        public int RenderHeight;
        public int Reserved;

        public FrameFlags FrameFlags { get { return (FrameFlags)Flags; } }
    }

    public enum SandboxState { Starting = 0, Calibrating = 1, Warmup = 2, Running = 3, Exiting = 4 }

    public enum SandboxCommand { None = 0, BenchmarkCamera = 1, RestartBenchmarkPath = 2 }

    /// <summary>
    /// Shared-memory telemetry channel between the sandbox game (writer) and the optimiser (reader).
    /// A named, pagefile-backed map on Windows (no files, no network, no firewall prompts);
    /// a small file-backed map elsewhere so the pieces can be tested off Windows.
    /// </summary>
    public sealed unsafe class TelemetryChannel : IDisposable
    {
        public const string MapName = "GAPO_Sandbox_Telemetry_v1";
        public const int Magic = 0x4F504147; // "GAPO"
        public const int Version = 1;
        public const int HeaderSize = 4096;
        public const int Capacity = 4096;
        public static readonly int RecordSize = Marshal.SizeOf(typeof(FrameRecord));
        public static readonly long TotalSize = HeaderSize + (long)Capacity * RecordSize;

        // Header offsets
        const int OffMagic = 0, OffVersion = 4, OffPid = 8, OffState = 12, OffFrames = 16, OffHeartbeat = 24,
            OffApplied = 32, OffInternalW = 36, OffInternalH = 40, OffClientW = 44, OffClientH = 48,
            OffEntities = 52, OffCamera = 56, OffContent = 60, OffCalibMs = 64,
            OffCmdSeq = 68, OffCmdId = 72, OffCmdArg = 76, OffCmdAck = 80, OffToastSeq = 84,
            OffSettingsPath = 96, OffToast = 608, OffTitle = 864;
        const int SettingsPathChars = 256, ToastChars = 128, TitleChars = 64;

        readonly MemoryMappedFile _map;
        readonly MemoryMappedViewAccessor _view;
        readonly FileStream _backingFile;
        byte* _base;

        TelemetryChannel(MemoryMappedFile map, FileStream backing)
        {
            _map = map;
            _backingFile = backing;
            _view = map.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);
            _base += _view.PointerOffset;
        }

        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        static string FallbackPath { get { return Path.Combine(Path.GetTempPath(), "gapo-telemetry-v1.bin"); } }

        public static TelemetryChannel CreateForWriter()
        {
            if (IsWindows)
            {
                var map = MemoryMappedFile.CreateOrOpen(MapName, TotalSize, MemoryMappedFileAccess.ReadWrite);
                var ch = new TelemetryChannel(map, null);
                ch.Clear();
                return ch;
            }
            var fs = new FileStream(FallbackPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            fs.SetLength(TotalSize);
            var m = MemoryMappedFile.CreateFromFile(fs, null, TotalSize, MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, true);
            var c = new TelemetryChannel(m, fs);
            c.Clear();
            return c;
        }

        /// <summary>Returns null when no sandbox is running.</summary>
        public static TelemetryChannel TryOpenForReader()
        {
            try
            {
                if (IsWindows)
                {
                    var map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
                    var ch = new TelemetryChannel(map, null);
                    if (ch.ReadInt(OffMagic) != Magic || ch.ReadInt(OffVersion) != Version) { ch.Dispose(); return null; }
                    return ch;
                }
                if (!File.Exists(FallbackPath)) return null;
                var fs = new FileStream(FallbackPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < TotalSize) { fs.Dispose(); return null; }
                var m = MemoryMappedFile.CreateFromFile(fs, null, TotalSize, MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, true);
                var c = new TelemetryChannel(m, fs);
                if (c.ReadInt(OffMagic) != Magic || c.ReadInt(OffVersion) != Version) { c.Dispose(); return null; }
                return c;
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        void Clear()
        {
            for (long i = 0; i < HeaderSize; i++) _base[i] = 0;
            WriteInt(OffVersion, Version);
            WriteInt(OffMagic, Magic);
        }

        int ReadInt(int off) { return Volatile.Read(ref *(int*)(_base + off)); }
        void WriteInt(int off, int v) { Volatile.Write(ref *(int*)(_base + off), v); }
        long ReadLong(int off) { return Volatile.Read(ref *(long*)(_base + off)); }
        void WriteLong(int off, long v) { Volatile.Write(ref *(long*)(_base + off), v); }

        string ReadString(int off, int maxChars)
        {
            char* p = (char*)(_base + off);
            int n = 0;
            while (n < maxChars && p[n] != '\0') n++;
            return new string(p, 0, n);
        }

        void WriteString(int off, int maxChars, string s)
        {
            char* p = (char*)(_base + off);
            int n = Math.Min(s == null ? 0 : s.Length, maxChars - 1);
            for (int i = 0; i < n; i++) p[i] = s[i];
            p[n] = '\0';
        }

        // ---------------------------------------------------------------- header properties

        public int Pid { get { return ReadInt(OffPid); } set { WriteInt(OffPid, value); } }
        public SandboxState State { get { return (SandboxState)ReadInt(OffState); } set { WriteInt(OffState, (int)value); } }
        public long FramesWritten { get { return ReadLong(OffFrames); } }
        public long HeartbeatTicks { get { return ReadLong(OffHeartbeat); } set { WriteLong(OffHeartbeat, value); } }
        public int AppliedRevision { get { return ReadInt(OffApplied); } set { WriteInt(OffApplied, value); } }
        public int InternalWidth { get { return ReadInt(OffInternalW); } set { WriteInt(OffInternalW, value); } }
        public int InternalHeight { get { return ReadInt(OffInternalH); } set { WriteInt(OffInternalH, value); } }
        public int ClientWidth { get { return ReadInt(OffClientW); } set { WriteInt(OffClientW, value); } }
        public int ClientHeight { get { return ReadInt(OffClientH); } set { WriteInt(OffClientH, value); } }
        public int EntityCount { get { return ReadInt(OffEntities); } set { WriteInt(OffEntities, value); } }
        public int CameraMode { get { return ReadInt(OffCamera); } set { WriteInt(OffCamera, value); } }
        public int ContentLevel { get { return ReadInt(OffContent); } set { WriteInt(OffContent, value); } }
        public float CalibratedUltraMs
        {
            get { int v = ReadInt(OffCalibMs); return *(float*)&v; }
            set { float f = value; WriteInt(OffCalibMs, *(int*)&f); }
        }
        public string SettingsPath { get { return ReadString(OffSettingsPath, SettingsPathChars); } set { WriteString(OffSettingsPath, SettingsPathChars, value); } }
        public string GameTitle { get { return ReadString(OffTitle, TitleChars); } set { WriteString(OffTitle, TitleChars, value); } }

        // ---------------------------------------------------------------- commands (optimiser -> game)

        public void SendCommand(SandboxCommand cmd, int arg)
        {
            WriteInt(OffCmdId, (int)cmd);
            WriteInt(OffCmdArg, arg);
            WriteInt(OffCmdSeq, ReadInt(OffCmdSeq) + 1);
        }

        public int CommandSeq { get { return ReadInt(OffCmdSeq); } }
        public int CommandAck { get { return ReadInt(OffCmdAck); } }

        /// <summary>Game side: returns a pending command (once), acknowledging it.</summary>
        public bool TryTakeCommand(ref int lastSeq, out SandboxCommand cmd, out int arg)
        {
            int seq = ReadInt(OffCmdSeq);
            cmd = SandboxCommand.None; arg = 0;
            if (seq == lastSeq) return false;
            cmd = (SandboxCommand)ReadInt(OffCmdId);
            arg = ReadInt(OffCmdArg);
            lastSeq = seq;
            WriteInt(OffCmdAck, seq);
            return true;
        }

        public void ShowToast(string text)
        {
            WriteString(OffToast, ToastChars, text);
            WriteInt(OffToastSeq, ReadInt(OffToastSeq) + 1);
        }

        public bool TryTakeToast(ref int lastSeq, out string text)
        {
            int seq = ReadInt(OffToastSeq);
            text = null;
            if (seq == lastSeq) return false;
            lastSeq = seq;
            text = ReadString(OffToast, ToastChars);
            return true;
        }

        // ---------------------------------------------------------------- frame ring buffer

        /// <summary>Writer: seqlock-style publish. The index is written last so readers never see torn records.</summary>
        public void WriteFrame(ref FrameRecord rec)
        {
            long index = rec.FrameIndex;
            byte* slot = _base + HeaderSize + (index % Capacity) * RecordSize;
            Volatile.Write(ref *(long*)slot, -1L);
            FrameRecord copy = rec;
            copy.FrameIndex = -1;
            *(FrameRecord*)slot = copy;
            Thread.MemoryBarrier();
            Volatile.Write(ref *(long*)slot, index);
            WriteLong(OffFrames, index + 1);
            WriteLong(OffHeartbeat, DateTime.UtcNow.Ticks);
        }

        /// <summary>Reader: copies every new frame after <paramref name="nextIndex"/>. Returns the number read.</summary>
        public int ReadNewFrames(ref long nextIndex, FrameRecord[] buffer)
        {
            long written = FramesWritten;
            if (nextIndex > written) nextIndex = written; // the game restarted
            if (written - nextIndex > Capacity - 16) nextIndex = written - (Capacity - 16);
            int count = 0;
            while (nextIndex < written && count < buffer.Length)
            {
                byte* slot = _base + HeaderSize + (nextIndex % Capacity) * RecordSize;
                long before = Volatile.Read(ref *(long*)slot);
                FrameRecord r = *(FrameRecord*)slot;
                Thread.MemoryBarrier();
                long after = Volatile.Read(ref *(long*)slot);
                if (before == nextIndex && after == nextIndex)
                {
                    r.FrameIndex = nextIndex;
                    buffer[count++] = r;
                }
                nextIndex++;
            }
            return count;
        }

        public void Dispose()
        {
            try
            {
                if (_base != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _base = null; }
                _view.Dispose();
                _map.Dispose();
                if (_backingFile != null) _backingFile.Dispose();
            }
            catch { }
        }
    }
}
