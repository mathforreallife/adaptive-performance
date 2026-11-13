using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Gapo.Sandbox.Contract;

[Flags]
public enum FrameFlags
{
    None = 0,
    Warmup = 1,
    Autosave = 2,
    GarbageCollect = 4,
    SettingsReload = 8,
    ManualCamera = 16,
    BenchmarkCamera = 32,
    TerrainEdit = 64,
    Calibrating = 128,
}

[StructLayout(LayoutKind.Explicit, Size = FrameRecord.SizeInBytes)]
public struct FrameRecord
{
    public const int SizeInBytes = 120;

    [FieldOffset(0)] public long FrameIndex;
    [FieldOffset(8)] public double TimeMs;
    [FieldOffset(16)] public float FrameMs;
    [FieldOffset(20)] public float WorkMs;
    [FieldOffset(24)] public float SimMs;
    [FieldOffset(28)] public float SimEntitiesMs;
    [FieldOffset(32)] public float SimPhysicsMs;
    [FieldOffset(36)] public float SimParticlesMs;
    [FieldOffset(40)] public float RenderMs;
    [FieldOffset(44)] public float TerrainMs;
    [FieldOffset(48)] public float ShadowMs;
    [FieldOffset(52)] public float WaterMs;
    [FieldOffset(56)] public float SkyMs;
    [FieldOffset(60)] public float FogMs;
    [FieldOffset(64)] public float SpritesMs;
    [FieldOffset(68)] public float PostMs;
    [FieldOffset(72)] public float PresentMs;
    [FieldOffset(76)] public float IoMs;
    [FieldOffset(80)] public float WaitMs;
    [FieldOffset(84)] public int Revision;
    [FieldOffset(88)] public int Flags;
    [FieldOffset(92)] public int EntitiesSimulated;
    [FieldOffset(96)] public int EntitiesTotal;
    [FieldOffset(100)] public int ParticlesActive;
    [FieldOffset(104)] public int RenderThreads;
    [FieldOffset(108)] public int RenderWidth;
    [FieldOffset(112)] public int RenderHeight;
    [FieldOffset(116)] public int Reserved;

    public readonly FrameFlags FrameFlags => (FrameFlags)Flags;
}

public enum SandboxState
{
    Starting = 0,
    Calibrating = 1,
    Warmup = 2,
    Running = 3,
    Exiting = 4,
}

public enum SandboxCommand
{
    None = 0,
    BenchmarkCamera = 1,
    RestartBenchmarkPath = 2,
    TriggerWarmup = 3,
}

public sealed unsafe class TelemetryChannel : IDisposable
{
    public const string DefaultMapName = "GAPO_Sandbox_Telemetry_v1";
    public const int Magic = 0x4F504147;
    public const int Version = 1;
    public const int HeaderSize = 4096;
    public const int Capacity = 4096;
    public const int RecordSize = FrameRecord.SizeInBytes;
    public const long TotalSize = HeaderSize + ((long)Capacity * RecordSize);

    private const int ReaderLapMargin = 16;
    private const long SlotBeingWritten = -1;

    private const int MagicOffset = 0;
    private const int VersionOffset = 4;
    private const int PidOffset = 8;
    private const int StateOffset = 12;
    private const int FramesWrittenOffset = 16;
    private const int HeartbeatOffset = 24;
    private const int AppliedRevisionOffset = 32;
    private const int EntityCountOffset = 52;
    private const int CameraModeOffset = 56;
    private const int ContentLevelOffset = 60;
    private const int CalibratedUltraMsOffset = 64;
    private const int CommandSequenceOffset = 68;
    private const int CommandIdOffset = 72;
    private const int CommandArgumentOffset = 76;
    private const int CommandAcknowledgedOffset = 80;
    private const int ToastSequenceOffset = 84;
    private const int SettingsPathOffset = 96;
    private const int ToastOffset = 608;
    private const int GameTitleOffset = 864;

    private const int SettingsPathCapacity = 256;
    private const int ToastCapacity = 128;
    private const int GameTitleCapacity = 64;

    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private byte* origin;

    private TelemetryChannel(MemoryMappedFile map)
    {
        this.map = map;
        view = map.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref origin);
        origin += view.PointerOffset;
    }

    public static TelemetryChannel CreateForWriter(string mapName = DefaultMapName)
    {
        var map = MemoryMappedFile.CreateOrOpen(mapName, TotalSize, MemoryMappedFileAccess.ReadWrite);
        var channel = new TelemetryChannel(map);
        channel.ResetHeader();
        return channel;
    }

    public static TelemetryChannel? TryOpenForReader(string mapName = DefaultMapName)
    {
        try
        {
            var map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite);
            var channel = new TelemetryChannel(map);
            if (channel.ReadInt(MagicOffset) == Magic && channel.ReadInt(VersionOffset) == Version)
            {
                return channel;
            }

            channel.Dispose();
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public int Pid
    {
        get => ReadInt(PidOffset);
        set => WriteInt(PidOffset, value);
    }

    public SandboxState State
    {
        get => (SandboxState)ReadInt(StateOffset);
        set => WriteInt(StateOffset, (int)value);
    }

    public long FramesWritten => ReadLong(FramesWrittenOffset);

    public long HeartbeatTicks => ReadLong(HeartbeatOffset);

    public int AppliedRevision
    {
        get => ReadInt(AppliedRevisionOffset);
        set => WriteInt(AppliedRevisionOffset, value);
    }

    public int EntityCount
    {
        get => ReadInt(EntityCountOffset);
        set => WriteInt(EntityCountOffset, value);
    }

    public int CameraMode
    {
        get => ReadInt(CameraModeOffset);
        set => WriteInt(CameraModeOffset, value);
    }

    public int ContentLevel
    {
        get => ReadInt(ContentLevelOffset);
        set => WriteInt(ContentLevelOffset, value);
    }

    public float CalibratedUltraMs
    {
        get => BitConverter.Int32BitsToSingle(ReadInt(CalibratedUltraMsOffset));
        set => WriteInt(CalibratedUltraMsOffset, BitConverter.SingleToInt32Bits(value));
    }

    public string SettingsPath
    {
        get => ReadString(SettingsPathOffset, SettingsPathCapacity);
        set => WriteString(SettingsPathOffset, SettingsPathCapacity, value);
    }

    public string GameTitle
    {
        get => ReadString(GameTitleOffset, GameTitleCapacity);
        set => WriteString(GameTitleOffset, GameTitleCapacity, value);
    }

    public int CommandSequence => ReadInt(CommandSequenceOffset);

    public int CommandAcknowledged => ReadInt(CommandAcknowledgedOffset);

    public void SendCommand(SandboxCommand command, int argument)
    {
        WriteInt(CommandIdOffset, (int)command);
        WriteInt(CommandArgumentOffset, argument);
        WriteInt(CommandSequenceOffset, ReadInt(CommandSequenceOffset) + 1);
    }

    public bool TryTakeCommand(ref int lastSequence, out SandboxCommand command, out int argument)
    {
        var sequence = ReadInt(CommandSequenceOffset);
        command = SandboxCommand.None;
        argument = 0;
        if (sequence == lastSequence)
        {
            return false;
        }

        command = (SandboxCommand)ReadInt(CommandIdOffset);
        argument = ReadInt(CommandArgumentOffset);
        lastSequence = sequence;
        WriteInt(CommandAcknowledgedOffset, sequence);
        return true;
    }

    public void ShowToast(string text)
    {
        WriteString(ToastOffset, ToastCapacity, text);
        WriteInt(ToastSequenceOffset, ReadInt(ToastSequenceOffset) + 1);
    }

    public bool TryTakeToast(ref int lastSequence, out string text)
    {
        var sequence = ReadInt(ToastSequenceOffset);
        text = string.Empty;
        if (sequence == lastSequence)
        {
            return false;
        }

        lastSequence = sequence;
        text = ReadString(ToastOffset, ToastCapacity);
        return true;
    }

    public void WriteFrame(ref FrameRecord record)
    {
        var index = record.FrameIndex;
        var slot = SlotFor(index);
        Volatile.Write(ref *(long*)slot, SlotBeingWritten);
        var unpublished = record;
        unpublished.FrameIndex = SlotBeingWritten;
        *(FrameRecord*)slot = unpublished;
        Thread.MemoryBarrier();
        Volatile.Write(ref *(long*)slot, index);
        WriteLong(FramesWrittenOffset, index + 1);
        WriteLong(HeartbeatOffset, DateTime.UtcNow.Ticks);
    }

    public int ReadNewFrames(ref long nextIndex, Span<FrameRecord> buffer)
    {
        var written = FramesWritten;
        if (nextIndex > written)
        {
            nextIndex = written;
        }

        var oldestSafeIndex = written - (Capacity - ReaderLapMargin);
        if (nextIndex < oldestSafeIndex)
        {
            nextIndex = oldestSafeIndex;
        }

        var count = 0;
        while (nextIndex < written && count < buffer.Length)
        {
            var slot = SlotFor(nextIndex);
            var indexBeforeCopy = Volatile.Read(ref *(long*)slot);
            var copy = *(FrameRecord*)slot;
            Thread.MemoryBarrier();
            var indexAfterCopy = Volatile.Read(ref *(long*)slot);
            if (indexBeforeCopy == nextIndex && indexAfterCopy == nextIndex)
            {
                copy.FrameIndex = nextIndex;
                buffer[count++] = copy;
            }

            nextIndex++;
        }

        return count;
    }

    public void Dispose()
    {
        if (origin != null)
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            origin = null;
        }

        view.Dispose();
        map.Dispose();
    }

    private void ResetHeader()
    {
        new Span<byte>(origin, HeaderSize).Clear();
        WriteInt(VersionOffset, Version);
        WriteInt(MagicOffset, Magic);
    }

    private byte* SlotFor(long frameIndex) => origin + HeaderSize + ((frameIndex % Capacity) * RecordSize);

    private int ReadInt(int offset) => Volatile.Read(ref *(int*)(origin + offset));

    private void WriteInt(int offset, int value) => Volatile.Write(ref *(int*)(origin + offset), value);

    private long ReadLong(int offset) => Volatile.Read(ref *(long*)(origin + offset));

    private void WriteLong(int offset, long value) => Volatile.Write(ref *(long*)(origin + offset), value);

    private string ReadString(int offset, int capacity)
    {
        var characters = new ReadOnlySpan<char>(origin + offset, capacity);
        var terminator = characters.IndexOf('\0');
        return new string(terminator < 0 ? characters : characters[..terminator]);
    }

    private void WriteString(int offset, int capacity, string text)
    {
        var destination = new Span<char>(origin + offset, capacity);
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(destination);
        destination[length] = '\0';
    }
}
