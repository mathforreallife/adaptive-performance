using System.Buffers.Binary;
using System.Text;

namespace Gapo.Core.Hardware;

public static class SmbiosMemoryReader
{
    private const byte MemoryDeviceType = 17;
    private const byte EndOfTableType = 127;
    private const int StructureHeaderBytes = 4;
    private const int SizeOffset = 0x0C;
    private const int DeviceLocatorOffset = 0x10;
    private const int MemoryTypeOffset = 0x12;
    private const int SpeedOffset = 0x15;
    private const int ManufacturerOffset = 0x17;
    private const int ExtendedSizeOffset = 0x1C;
    private const int ConfiguredSpeedOffset = 0x20;
    private const ushort SizeUnknown = 0xFFFF;
    private const ushort SizeIsInExtendedField = 0x7FFF;
    private const ushort SizeInKilobytesFlag = 0x8000;
    private const ushort SpeedUnknown = 0xFFFF;
    private const long Kilobyte = 1024;
    private const long Megabyte = 1024 * 1024;

    public static IReadOnlyList<MemoryModule> ReadModules(ReadOnlySpan<byte> smbiosTable)
    {
        var modules = new List<MemoryModule>();
        var offset = 0;
        while (offset + StructureHeaderBytes <= smbiosTable.Length)
        {
            var type = smbiosTable[offset];
            var formattedLength = smbiosTable[offset + 1];
            if (formattedLength < StructureHeaderBytes || offset + formattedLength > smbiosTable.Length)
            {
                break;
            }

            var formatted = smbiosTable.Slice(offset, formattedLength);
            var stringsStart = offset + formattedLength;
            var next = EndOfStrings(smbiosTable, stringsStart);
            if (type == MemoryDeviceType && TryReadModule(formatted, smbiosTable[stringsStart..next], out var module))
            {
                modules.Add(module);
            }

            if (type == EndOfTableType)
            {
                break;
            }

            offset = next;
        }

        return modules;
    }

    private static bool TryReadModule(ReadOnlySpan<byte> formatted, ReadOnlySpan<byte> strings, out MemoryModule module)
    {
        module = new MemoryModule(0, 0, 0, string.Empty, string.Empty, string.Empty);
        var capacity = ReadCapacityBytes(formatted);
        if (capacity <= 0)
        {
            return false;
        }

        module = new MemoryModule(
            capacity,
            ReadSpeed(formatted, SpeedOffset),
            ReadSpeed(formatted, ConfiguredSpeedOffset),
            MemoryTypeName(ReadByte(formatted, MemoryTypeOffset)),
            ReadString(strings, ReadByte(formatted, DeviceLocatorOffset)),
            ReadString(strings, ReadByte(formatted, ManufacturerOffset)));
        return true;
    }

    private static long ReadCapacityBytes(ReadOnlySpan<byte> formatted)
    {
        var size = ReadWord(formatted, SizeOffset);
        if (size is 0 or SizeUnknown)
        {
            return 0;
        }

        if (size == SizeIsInExtendedField)
        {
            return formatted.Length >= ExtendedSizeOffset + sizeof(uint)
                ? (BinaryPrimitives.ReadUInt32LittleEndian(formatted[ExtendedSizeOffset..]) & 0x7FFFFFFF) * Megabyte
                : 0;
        }

        return (size & SizeInKilobytesFlag) != 0 ? (size & ~SizeInKilobytesFlag) * Kilobyte : size * Megabyte;
    }

    private static int ReadSpeed(ReadOnlySpan<byte> formatted, int offset)
    {
        var speed = ReadWord(formatted, offset);
        return speed == SpeedUnknown ? 0 : speed;
    }

    private static byte ReadByte(ReadOnlySpan<byte> formatted, int offset) =>
        offset < formatted.Length ? formatted[offset] : (byte)0;

    private static ushort ReadWord(ReadOnlySpan<byte> formatted, int offset) =>
        offset + sizeof(ushort) <= formatted.Length ? BinaryPrimitives.ReadUInt16LittleEndian(formatted[offset..]) : (ushort)0;

    private static int EndOfStrings(ReadOnlySpan<byte> table, int stringsStart)
    {
        for (var index = stringsStart; index + 1 < table.Length; index++)
        {
            if (table[index] == 0 && table[index + 1] == 0)
            {
                return index + 2;
            }
        }

        return table.Length;
    }

    private static string ReadString(ReadOnlySpan<byte> strings, byte oneBasedIndex)
    {
        if (oneBasedIndex == 0)
        {
            return string.Empty;
        }

        var current = 1;
        var start = 0;
        for (var index = 0; index < strings.Length; index++)
        {
            if (strings[index] != 0)
            {
                continue;
            }

            if (current == oneBasedIndex)
            {
                return Encoding.ASCII.GetString(strings[start..index]).Trim();
            }

            current++;
            start = index + 1;
        }

        return string.Empty;
    }

    private static string MemoryTypeName(byte smbiosMemoryType) => smbiosMemoryType switch
    {
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => string.Empty,
    };
}
