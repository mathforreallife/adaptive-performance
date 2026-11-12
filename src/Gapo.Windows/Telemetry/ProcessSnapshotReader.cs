using System.Runtime.InteropServices;
using Gapo.Core.Telemetry;

namespace Gapo.Windows.Telemetry;

public sealed unsafe partial class ProcessSnapshotReader : IDisposable
{
    private const int SystemProcessInformation = 5;
    private const int Success = 0;
    private const int InformationLengthMismatch = unchecked((int)0xC0000004);
    private const uint InitialBufferBytes = 512 * 1024;
    private const uint BufferHeadroomBytes = 64 * 1024;
    private const int MaximumGrowAttempts = 6;
    private const string IdleProcessName = "Idle";
    private const int IdleProcessId = 0;

    private const int NextEntryOffset = 0;
    private const int ThreadCountOffset = 4;
    private const int CycleCountOffset = 24;
    private const int CreateTimeOffset = 32;
    private const int UserTimeOffset = 40;
    private const int KernelTimeOffset = 48;
    private const int ImageNameLengthOffset = 56;
    private const int ImageNameBufferOffset = 64;
    private const int ProcessIdOffset = 80;
    private const int ParentProcessIdOffset = 88;
    private const int SessionIdOffset = 100;
    private const int WorkingSetOffset = 144;
    private const int PrivateBytesOffset = 200;
    private const int ReadBytesOffset = 232;
    private const int WriteBytesOffset = 240;
    private const int FirstThreadOffset = 256;

    private const int ThreadEntryBytes = 80;
    private const int ThreadKernelTimeOffset = 0;
    private const int ThreadUserTimeOffset = 8;
    private const int ThreadIdOffset = 48;

    private readonly Dictionary<(int ProcessId, long CreateTime), string> nameCache = new();
    private byte* buffer;
    private uint bufferBytes;

    public ProcessSnapshotReader()
    {
        if (nint.Size != sizeof(long))
        {
            throw new PlatformNotSupportedException("The process snapshot layout is defined for 64-bit Windows only.");
        }

        Allocate(InitialBufferBytes);
    }

    public void Capture(ProcessSnapshot destination, TimeSpan capturedAt, int detailedProcessId)
    {
        QueryIntoBuffer();
        destination.Begin(capturedAt, detailedProcessId);
        var liveProcesses = 0;
        for (var entry = buffer; ; entry += ReadUInt32(entry, NextEntryOffset))
        {
            var process = ReadProcess(entry);
            destination.Add(process);
            liveProcesses++;
            if (process.ProcessId == detailedProcessId && detailedProcessId != IdleProcessId)
            {
                ReadThreads(entry, process.ThreadCount, destination);
            }

            if (ReadUInt32(entry, NextEntryOffset) == 0)
            {
                break;
            }
        }

        if (nameCache.Count > liveProcesses * 2)
        {
            nameCache.Clear();
        }
    }

    public void Dispose()
    {
        if (buffer != null)
        {
            NativeMemory.Free(buffer);
            buffer = null;
        }
    }

    private void QueryIntoBuffer()
    {
        ObjectDisposedException.ThrowIf(buffer == null, this);
        for (var attempt = 0; attempt < MaximumGrowAttempts; attempt++)
        {
            var status = NtQuerySystemInformation(SystemProcessInformation, buffer, bufferBytes, out var requiredBytes);
            if (status == Success)
            {
                return;
            }

            if (status != InformationLengthMismatch)
            {
                throw new InvalidOperationException($"NtQuerySystemInformation failed with status 0x{status:X8}.");
            }

            NativeMemory.Free(buffer);
            Allocate(Math.Max(requiredBytes, bufferBytes) + BufferHeadroomBytes);
        }

        throw new InvalidOperationException("The process list kept growing faster than the snapshot buffer.");
    }

    private void Allocate(uint bytes)
    {
        buffer = (byte*)NativeMemory.Alloc(bytes);
        bufferBytes = bytes;
    }

    private ProcessSample ReadProcess(byte* entry)
    {
        var processId = (int)ReadInt64(entry, ProcessIdOffset);
        var createTime = ReadInt64(entry, CreateTimeOffset);
        return new ProcessSample(
            processId,
            (int)ReadInt64(entry, ParentProcessIdOffset),
            (int)ReadUInt32(entry, SessionIdOffset),
            NameOf(entry, processId, createTime),
            createTime,
            (ulong)ReadInt64(entry, CycleCountOffset),
            ReadInt64(entry, KernelTimeOffset),
            ReadInt64(entry, UserTimeOffset),
            ReadInt64(entry, PrivateBytesOffset),
            ReadInt64(entry, WorkingSetOffset),
            ReadInt64(entry, ReadBytesOffset),
            ReadInt64(entry, WriteBytesOffset),
            (int)ReadUInt32(entry, ThreadCountOffset));
    }

    private static void ReadThreads(byte* entry, int threadCount, ProcessSnapshot destination)
    {
        for (var index = 0; index < threadCount; index++)
        {
            var thread = entry + FirstThreadOffset + (index * ThreadEntryBytes);
            destination.AddDetailedThread(new ThreadSample(
                (int)ReadInt64(thread, ThreadIdOffset),
                ReadInt64(thread, ThreadKernelTimeOffset),
                ReadInt64(thread, ThreadUserTimeOffset)));
        }
    }

    private string NameOf(byte* entry, int processId, long createTime)
    {
        if (nameCache.TryGetValue((processId, createTime), out var cached))
        {
            return cached;
        }

        var lengthInBytes = *(ushort*)(entry + ImageNameLengthOffset);
        var characters = (char*)ReadInt64(entry, ImageNameBufferOffset);
        var name = characters == null || lengthInBytes == 0
            ? (processId == IdleProcessId ? IdleProcessName : string.Empty)
            : new string(characters, 0, lengthInBytes / sizeof(char));
        nameCache[(processId, createTime)] = name;
        return name;
    }

    private static uint ReadUInt32(byte* entry, int offset) => *(uint*)(entry + offset);

    private static long ReadInt64(byte* entry, int offset) => *(long*)(entry + offset);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int informationClass, void* information, uint length, out uint returnedLength);
}
