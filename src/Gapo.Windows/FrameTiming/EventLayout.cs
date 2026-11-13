using System.Runtime.InteropServices;

namespace Gapo.Windows.FrameTiming;

internal sealed unsafe partial class EventLayout
{
    private const uint InsufficientBuffer = 122;
    private const int PropertyCountOffset = 100;
    private const int TopLevelPropertyCountOffset = 104;
    private const int PropertyArrayOffset = 112;
    private const int PropertyInfoBytes = 24;
    private const uint StructFlag = 0x1;
    private const uint ParamLengthFlag = 0x2;
    private const uint ParamCountFlag = 0x4;
    private const uint FixedCountFlag = 0x20;

    private readonly Dictionary<string, (int Offset, int Size)> fields;

    private EventLayout(Dictionary<string, (int Offset, int Size)> fields)
    {
        this.fields = fields;
    }

    public static EventLayout? Read(EtwNative.EventRecord* record)
    {
        uint size = 0;
        if (TdhGetEventInformation(record, 0, null, null, ref size) != InsufficientBuffer || size == 0)
        {
            return null;
        }

        var buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            if (TdhGetEventInformation(record, 0, null, buffer, ref size) != EtwNative.Success)
            {
                return null;
            }

            var pointerBytes = (record->Flags & EtwNative.ThirtyTwoBitHeaderFlag) != 0 ? sizeof(uint) : sizeof(ulong);
            var topLevel = *(uint*)(buffer + TopLevelPropertyCountOffset);
            var all = *(uint*)(buffer + PropertyCountOffset);
            var fields = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            var offset = 0;
            for (var index = 0; index < topLevel && index < all; index++)
            {
                var property = buffer + PropertyArrayOffset + (index * PropertyInfoBytes);
                var flags = *(uint*)property;
                var name = Marshal.PtrToStringUni((nint)(buffer + *(uint*)(property + 4))) ?? string.Empty;
                var inType = *(ushort*)(property + 8);
                var count = (flags & FixedCountFlag) != 0 ? *(ushort*)(property + 16) : 1;
                var length = *(ushort*)(property + 18);
                if ((flags & (StructFlag | ParamLengthFlag | ParamCountFlag)) != 0 || SizeOf(inType, pointerBytes, length) is not { } itemSize)
                {
                    break;
                }

                fields[name] = (offset, itemSize);
                offset += itemSize * Math.Max(1, (int)count);
            }

            return new EventLayout(fields);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    public ulong Unsigned(EtwNative.EventRecord* record, string name)
    {
        if (!fields.TryGetValue(name, out var field) || field.Offset + field.Size > record->UserDataLength || record->UserData == null)
        {
            return 0;
        }

        var at = record->UserData + field.Offset;
        return field.Size switch
        {
            1 => *at,
            2 => *(ushort*)at,
            4 => *(uint*)at,
            8 => *(ulong*)at,
            _ => 0,
        };
    }

    public bool Has(string name) => fields.ContainsKey(name);

    private static int? SizeOf(ushort inType, int pointerBytes, ushort length) => inType switch
    {
        3 or 4 => 1,
        5 or 6 => 2,
        7 or 8 or 13 or 20 => 4,
        9 or 10 or 12 or 17 or 21 => 8,
        11 => 4,
        15 => 16,
        16 => pointerBytes,
        14 when length > 0 => length,
        _ => null,
    };

    [LibraryImport("tdh.dll")]
    private static partial uint TdhGetEventInformation(EtwNative.EventRecord* record, uint contextCount, void* context, byte* buffer, ref uint bufferSize);
}
