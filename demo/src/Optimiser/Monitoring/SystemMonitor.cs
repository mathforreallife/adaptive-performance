using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Gapo.Optimiser.Hardware;

namespace Gapo.Optimiser.Monitoring
{
    public sealed class MonitorSample
    {
        public double TimeMs;
        public float[] CoreUsage = new float[0];   // 0..1 per logical processor
        public float TotalCpuPct;                  // whole machine
        public int MemLoadPct;
        public long AvailBytes;
        public float CommitPct;
        public float GameCpuPct;                   // % of the whole machine
        public float GameCpuCores;                 // in "cores" (1.0 = one core fully busy)
        public float GameHottestThreadPct = -1;    // % of one core; -1 = not sampled
        public int GameBusyThreads = -1;
        public float GameReadMBps, GameWriteMBps;
        public long GamePrivateBytes, GameWorkingSet;
        public float BackgroundCpuPct;             // machine CPU not used by the game or by GAPO
        public float SelfCpuPct;                   // GAPO itself, % of the whole machine
        public long SelfPrivateBytes, SelfWorkingSet;
        public int SelfThreads;
    }

    /// <summary>
    /// Low-overhead system sampler. One sample per second from cheap calls only
    /// (NtQuerySystemInformation per-core times, GlobalMemoryStatusEx, GetProcessTimes/IO counters on
    /// cached handles). The expensive system-wide process snapshot (for per-thread times) is taken only
    /// when the engine asks for it, and never inside a measurement window.
    /// </summary>
    public sealed class SystemMonitor : IDisposable
    {
        readonly int _logical = Environment.ProcessorCount;
        readonly Thread _thread;
        volatile bool _stop;
        readonly object _lock = new object();
        readonly List<MonitorSample> _history = new List<MonitorSample>(180);
        readonly Stopwatch _clock = Stopwatch.StartNew();
        public double Now { get { return _clock.Elapsed.TotalMilliseconds; } }

        // Per-core
        readonly IntPtr _coreBuf;
        readonly int _coreBufLen;
        long[] _prevIdle, _prevTotal;

        // Game process
        int _gamePid;
        IntPtr _gameHandle = IntPtr.Zero;
        long _prevGameCpu, _prevGameRead, _prevGameWrite;
        double _prevGameAt;
        readonly Dictionary<long, long> _prevThreadCpu = new Dictionary<long, long>();
        double _prevThreadAt;

        // Self
        readonly IntPtr _self = Gapo.Shared.Native.IsWindows ? NativeMethods.GetCurrentProcess() : IntPtr.Zero;
        long _prevSelfCpu;
        double _prevSelfAt;
        long _selfCpuAtMark; double _markAt;

        public volatile bool ThreadSampling;      // engine turns this on while diagnosing
        public int IntervalMs = 1000;
        public MonitorSample Latest { get; private set; }
        public double SelfCpuPeakPct, SelfPrivatePeakMB;

        public SystemMonitor()
        {
            _coreBufLen = Marshal.SizeOf(typeof(NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION)) * Math.Max(1, _logical);
            _coreBuf = Marshal.AllocHGlobal(_coreBufLen);
            _prevIdle = new long[_logical]; _prevTotal = new long[_logical];
            Latest = new MonitorSample { CoreUsage = new float[_logical] };
            _thread = new Thread(Loop) { IsBackground = true, Name = "GAPO monitor", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void SetGame(int pid)
        {
            lock (_lock)
            {
                if (pid == _gamePid) return;
                if (_gameHandle != IntPtr.Zero) { NativeMethods.CloseHandle(_gameHandle); _gameHandle = IntPtr.Zero; }
                _gamePid = pid;
                _prevThreadCpu.Clear();
                _prevGameAt = 0;
                if (pid > 0 && Gapo.Shared.Native.IsWindows)
                {
                    _gameHandle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | 0x0010, false, pid);
                    if (_gameHandle == IntPtr.Zero) _gameHandle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                }
            }
        }

        public List<MonitorSample> History(double sinceMs)
        {
            lock (_lock) return _history.FindAll(s => s.TimeMs >= sinceMs);
        }

        /// <summary>Starts a self-overhead measurement span (e.g. one measurement window).</summary>
        public void MarkSelf() { _selfCpuAtMark = SelfCpuTicks(); _markAt = Now; }

        /// <summary>GAPO's own CPU use since <see cref="MarkSelf"/>, as % of the whole machine.</summary>
        public double SelfCpuSinceMarkPct()
        {
            double elapsed = Now - _markAt;
            if (elapsed <= 0) return 0;
            return (SelfCpuTicks() - _selfCpuAtMark) / 10000.0 / elapsed / _logical * 100.0;
        }

        long SelfCpuTicks()
        {
            if (!Gapo.Shared.Native.IsWindows) using (var p = Process.GetCurrentProcess()) return p.TotalProcessorTime.Ticks;
            long c, e, k, u;
            return NativeMethods.GetProcessTimes(_self, out c, out e, out k, out u) ? k + u : 0;
        }

        void Loop()
        {
            while (!_stop)
            {
                try { Sample(); } catch { }
                int waited = 0;
                while (!_stop && waited < IntervalMs) { Thread.Sleep(100); waited += 100; }
            }
        }

        void Sample()
        {
            double now = Now;
            var s = new MonitorSample { TimeMs = now, CoreUsage = new float[_logical] };

            if (!Gapo.Shared.Native.IsWindows) { SampleFallback(s, now); return; }

            // Per-core load.
            int ret;
            if (NativeMethods.NtQuerySystemInformation(8, _coreBuf, _coreBufLen, out ret) == 0)
            {
                int size = Marshal.SizeOf(typeof(NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
                float sum = 0;
                for (int i = 0; i < _logical; i++)
                {
                    var p = (NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION)Marshal.PtrToStructure(_coreBuf + i * size, typeof(NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
                    long total = p.KernelTime + p.UserTime;   // kernel time includes idle time
                    long dTotal = total - _prevTotal[i], dIdle = p.IdleTime - _prevIdle[i];
                    float u = dTotal > 0 ? 1f - (float)dIdle / dTotal : 0f;
                    s.CoreUsage[i] = u < 0 ? 0 : (u > 1 ? 1 : u);
                    sum += s.CoreUsage[i];
                    _prevTotal[i] = total; _prevIdle[i] = p.IdleTime;
                }
                s.TotalCpuPct = sum / _logical * 100f;
            }

            var m = NativeMethods.Memory();
            s.MemLoadPct = (int)m.dwMemoryLoad;
            s.AvailBytes = (long)m.ullAvailPhys;
            s.CommitPct = m.ullTotalPageFile > 0 ? 100f * (m.ullTotalPageFile - m.ullAvailPageFile) / m.ullTotalPageFile : 0;

            // Game process: CPU, IO, memory through one cached handle.
            lock (_lock)
            {
                if (_gameHandle != IntPtr.Zero)
                {
                    long c, e, k, u;
                    int exit;
                    if (NativeMethods.GetExitCodeProcess(_gameHandle, out exit) && exit != 259) { NativeMethods.CloseHandle(_gameHandle); _gameHandle = IntPtr.Zero; _gamePid = 0; }
                    else if (NativeMethods.GetProcessTimes(_gameHandle, out c, out e, out k, out u))
                    {
                        NativeMethods.IO_COUNTERS io;
                        bool haveIo = NativeMethods.GetProcessIoCounters(_gameHandle, out io);
                        if (_prevGameAt > 0)
                        {
                            double dt = now - _prevGameAt;
                            s.GameCpuCores = (float)((k + u - _prevGameCpu) / 10000.0 / dt);
                            s.GameCpuPct = s.GameCpuCores / _logical * 100f;
                            if (haveIo)
                            {
                                s.GameReadMBps = (float)(((long)io.ReadTransferCount - _prevGameRead) / 1048576.0 / (dt / 1000.0));
                                s.GameWriteMBps = (float)(((long)io.WriteTransferCount - _prevGameWrite) / 1048576.0 / (dt / 1000.0));
                            }
                        }
                        _prevGameCpu = k + u; _prevGameAt = now;
                        if (haveIo) { _prevGameRead = (long)io.ReadTransferCount; _prevGameWrite = (long)io.WriteTransferCount; }
                        PsApi.Counters(_gameHandle, out s.GamePrivateBytes, out s.GameWorkingSet);
                    }
                }
            }

            // Hottest game thread — only while the engine is diagnosing (costs a system-wide snapshot).
            if (ThreadSampling && _gamePid > 0 && now - _prevThreadAt >= 900)
            {
                var snap = ProcessSnapshot.Take(_gamePid);
                if (snap != null && snap.GameThreads != null)
                {
                    double dt = now - _prevThreadAt;
                    float hottest = 0; int busy = 0;
                    if (_prevThreadAt > 0)
                        foreach (var t in snap.GameThreads)
                        {
                            long prev;
                            if (_prevThreadCpu.TryGetValue(t.Key, out prev))
                            {
                                float pct = (float)((t.Value - prev) / 10000.0 / dt * 100.0);
                                if (pct > hottest) hottest = pct;
                                if (pct > 10f) busy++;
                            }
                        }
                    _prevThreadCpu.Clear();
                    foreach (var t in snap.GameThreads) _prevThreadCpu[t.Key] = t.Value;
                    if (_prevThreadAt > 0) { s.GameHottestThreadPct = Math.Min(100f, hottest); s.GameBusyThreads = busy; }
                    _prevThreadAt = now;
                }
            }
            else if (!ThreadSampling) _prevThreadAt = 0;

            // GAPO's own overhead.
            long self = SelfCpuTicks();
            if (_prevSelfAt > 0) s.SelfCpuPct = (float)((self - _prevSelfCpu) / 10000.0 / (now - _prevSelfAt) / _logical * 100.0);
            _prevSelfCpu = self; _prevSelfAt = now;
            PsApi.Counters(_self, out s.SelfPrivateBytes, out s.SelfWorkingSet);
            s.BackgroundCpuPct = Math.Max(0f, s.TotalCpuPct - s.GameCpuPct - s.SelfCpuPct);
            if (s.SelfCpuPct > SelfCpuPeakPct) SelfCpuPeakPct = s.SelfCpuPct;
            if (s.SelfPrivateBytes / 1048576.0 > SelfPrivatePeakMB) SelfPrivatePeakMB = s.SelfPrivateBytes / 1048576.0;

            lock (_lock)
            {
                _history.Add(s);
                if (_history.Count > 180) _history.RemoveRange(0, _history.Count - 180);
                Latest = s;
            }
        }

        /// <summary>Portable fallback (used only when testing off Windows).</summary>
        void SampleFallback(MonitorSample s, double now)
        {
            long self = SelfCpuTicks();
            if (_prevSelfAt > 0) s.SelfCpuPct = (float)((self - _prevSelfCpu) / 10000.0 / (now - _prevSelfAt) / _logical * 100.0);
            _prevSelfCpu = self; _prevSelfAt = now;
            using (var p = Process.GetCurrentProcess()) { s.SelfPrivateBytes = p.PrivateMemorySize64; s.SelfWorkingSet = p.WorkingSet64; }
            if (_gamePid > 0)
                try
                {
                    using (var g = Process.GetProcessById(_gamePid))
                    {
                        long cpu = g.TotalProcessorTime.Ticks;
                        if (_prevGameAt > 0) { s.GameCpuCores = (float)((cpu - _prevGameCpu) / 10000.0 / (now - _prevGameAt)); s.GameCpuPct = s.GameCpuCores / _logical * 100f; }
                        _prevGameCpu = cpu; _prevGameAt = now;
                    }
                }
                catch { }
            lock (_lock) { _history.Add(s); if (_history.Count > 180) _history.RemoveAt(0); Latest = s; }
        }

        public void Dispose()
        {
            _stop = true;
            try { _thread.Join(1500); } catch { }
            lock (_lock) { if (_gameHandle != IntPtr.Zero) NativeMethods.CloseHandle(_gameHandle); _gameHandle = IntPtr.Zero; }
            Marshal.FreeHGlobal(_coreBuf);
        }
    }

    static class PsApi
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }
        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
        static extern bool GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS_EX c, int cb);

        public static void Counters(IntPtr h, out long privateBytes, out long workingSet)
        {
            privateBytes = 0; workingSet = 0;
            try
            {
                PROCESS_MEMORY_COUNTERS_EX c;
                if (GetProcessMemoryInfo(h, out c, Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX))))
                { privateBytes = (long)c.PrivateUsage.ToUInt64(); workingSet = (long)c.WorkingSetSize.ToUInt64(); }
            }
            catch { }
        }
    }

    public sealed class ProcessEntry
    {
        public int Pid;
        public string Name;
        public long CpuTicks;          // user + kernel, 100 ns units
        public long WorkingSet, PrivateBytes;
    }

    /// <summary>
    /// One NtQuerySystemInformation(SystemProcessInformation) call: every process's CPU time and memory,
    /// plus per-thread CPU times of one chosen process, without opening a single process handle.
    /// </summary>
    public sealed class ProcessSnapshot
    {
        public List<ProcessEntry> Processes = new List<ProcessEntry>();
        public List<KeyValuePair<long, long>> GameThreads;   // thread id -> cpu ticks

        static IntPtr _buf = IntPtr.Zero;
        static int _bufLen;
        static readonly object Gate = new object();

        public static ProcessSnapshot Take(int gamePid)
        {
            if (IntPtr.Size != 8) return TakeManaged(gamePid);  // struct offsets below are for 64-bit
            try { return TakeNative(gamePid); }
            catch (DllNotFoundException) { return TakeManaged(gamePid); }
            catch (EntryPointNotFoundException) { return TakeManaged(gamePid); }
        }

        static ProcessSnapshot TakeNative(int gamePid)
        {
            lock (Gate)
            {
                if (_buf == IntPtr.Zero) { _bufLen = 512 * 1024; _buf = Marshal.AllocHGlobal(_bufLen); }
                int needed, status;
                for (int attempt = 0; ; attempt++)
                {
                    status = NativeMethods.NtQuerySystemInformation(5, _buf, _bufLen, out needed);
                    if (status != unchecked((int)0xC0000004) || attempt > 4) break;
                    Marshal.FreeHGlobal(_buf);
                    _bufLen = Math.Max(needed, _bufLen) + 128 * 1024;
                    _buf = Marshal.AllocHGlobal(_bufLen);
                }
                if (status != 0) return TakeManaged(gamePid);
                var snap = new ProcessSnapshot();
                int off = 0;
                while (true)
                {
                    IntPtr p = _buf + off;
                    int next = Marshal.ReadInt32(p, 0);
                    int threads = Marshal.ReadInt32(p, 4);
                    if (next != 0 && 256 + threads * 80 > next) return TakeManaged(gamePid); // unexpected layout
                    int pid = (int)Marshal.ReadInt64(p, 80);
                    ushort nameLen = (ushort)Marshal.ReadInt16(p, 56);
                    IntPtr namePtr = Marshal.ReadIntPtr(p, 64);
                    string name = pid == 0 ? "System Idle" : (namePtr == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(namePtr, nameLen / 2));
                    var e = new ProcessEntry
                    {
                        Pid = pid,
                        Name = name,
                        CpuTicks = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48),
                        WorkingSet = Marshal.ReadInt64(p, 144),
                        PrivateBytes = Marshal.ReadInt64(p, 200),
                    };
                    snap.Processes.Add(e);
                    if (pid == gamePid && gamePid > 0)
                    {
                        snap.GameThreads = new List<KeyValuePair<long, long>>(threads);
                        for (int t = 0; t < threads; t++)
                        {
                            IntPtr tp = p + 256 + t * 80;
                            long cpu = Marshal.ReadInt64(tp, 0) + Marshal.ReadInt64(tp, 8);
                            long tid = Marshal.ReadInt64(tp, 48);
                            snap.GameThreads.Add(new KeyValuePair<long, long>(tid, cpu));
                        }
                    }
                    if (next == 0) break;
                    off += next;
                }
                return snap;
            }
        }

        static ProcessSnapshot TakeManaged(int gamePid)
        {
            var snap = new ProcessSnapshot();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var e = new ProcessEntry { Pid = p.Id, Name = p.ProcessName, WorkingSet = p.WorkingSet64, PrivateBytes = p.PrivateMemorySize64 };
                    try { e.CpuTicks = p.TotalProcessorTime.Ticks; } catch { }
                    snap.Processes.Add(e);
                    if (p.Id == gamePid)
                    {
                        snap.GameThreads = new List<KeyValuePair<long, long>>();
                        foreach (ProcessThread t in p.Threads)
                            try { snap.GameThreads.Add(new KeyValuePair<long, long>(t.Id, t.TotalProcessorTime.Ticks)); } catch { }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return snap;
        }
    }
}
