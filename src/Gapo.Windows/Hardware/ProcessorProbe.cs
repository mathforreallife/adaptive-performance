using System.Runtime.InteropServices;
using Gapo.Core.Hardware;
using Microsoft.Win32;

namespace Gapo.Windows.Hardware;

internal static unsafe class ProcessorProbe
{
    private const string FirstProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const int ProcessorCoreRelation = 0;
    private const int CacheRelation = 2;
    private const byte SimultaneousMultithreadingFlag = 1;
    private const int InstructionCache = 1;
    private const int Kilobyte = 1024;

    internal static CpuFacts Read()
    {
        var facts = new CpuFacts
        {
            LogicalProcessors = Environment.ProcessorCount,
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
        facts = ReadIdentity(facts);
        facts = ReadTopology(facts);
        return ReadClocks(facts);
    }

    private static CpuFacts ReadIdentity(CpuFacts facts)
    {
        using var key = Registry.LocalMachine.OpenSubKey(FirstProcessorKey);
        if (key is null)
        {
            return facts;
        }

        var name = (key.GetValue("ProcessorNameString") as string ?? facts.Name).Trim();
        while (name.Contains("  ", StringComparison.Ordinal))
        {
            name = name.Replace("  ", " ", StringComparison.Ordinal);
        }

        return facts with
        {
            Name = name,
            Vendor = (key.GetValue("VendorIdentifier") as string ?? string.Empty).Trim(),
            BaseMegahertz = key.GetValue("~MHz") is int megahertz ? megahertz : 0,
        };
    }

    private static CpuFacts ReadTopology(CpuFacts facts)
    {
        uint length = 0;
        HardwareNative.GetLogicalProcessorInformationEx(HardwareNative.RelationAll, null, ref length);
        if (length == 0)
        {
            return facts with { Cores = facts.LogicalProcessors };
        }

        var buffer = (byte*)NativeMemory.Alloc(length);
        try
        {
            if (!HardwareNative.GetLogicalProcessorInformationEx(HardwareNative.RelationAll, buffer, ref length))
            {
                return facts with { Cores = facts.LogicalProcessors };
            }

            return ParseTopology(facts, new ReadOnlySpan<byte>(buffer, (int)length));
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static CpuFacts ParseTopology(CpuFacts facts, ReadOnlySpan<byte> entries)
    {
        var efficiencyClasses = new List<byte>();
        var anyCoreHasTwoThreads = false;
        long level2Bytes = 0;
        long level3Bytes = 0;
        for (var offset = 0; offset + 8 <= entries.Length;)
        {
            var relation = BitConverter.ToInt32(entries[offset..]);
            var size = BitConverter.ToInt32(entries[(offset + 4)..]);
            if (size <= 0)
            {
                break;
            }

            if (relation == ProcessorCoreRelation)
            {
                anyCoreHasTwoThreads |= (entries[offset + 8] & SimultaneousMultithreadingFlag) != 0;
                efficiencyClasses.Add(entries[offset + 9]);
            }
            else if (relation == CacheRelation)
            {
                var level = entries[offset + 8];
                var cacheBytes = BitConverter.ToUInt32(entries[(offset + 12)..]);
                var type = BitConverter.ToInt32(entries[(offset + 16)..]);
                level2Bytes += level == 2 && type != InstructionCache ? cacheBytes : 0;
                level3Bytes += level == 3 ? cacheBytes : 0;
            }

            offset += size;
        }

        var cores = efficiencyClasses.Count > 0 ? efficiencyClasses.Count : facts.LogicalProcessors;
        var highestClass = efficiencyClasses.Count > 0 ? efficiencyClasses.Max() : (byte)0;
        var isHybrid = efficiencyClasses.Count > 0 && highestClass > efficiencyClasses.Min();
        var performanceCores = isHybrid ? efficiencyClasses.Count(efficiencyClass => efficiencyClass == highestClass) : 0;
        return facts with
        {
            Cores = cores,
            HasSimultaneousMultithreading = anyCoreHasTwoThreads || facts.LogicalProcessors > cores,
            IsHybrid = isHybrid,
            PerformanceCores = performanceCores,
            EfficiencyCores = isHybrid ? cores - performanceCores : 0,
            Level2CacheKilobytes = (int)(level2Bytes / Kilobyte),
            Level3CacheKilobytes = (int)(level3Bytes / Kilobyte),
        };
    }

    private static CpuFacts ReadClocks(CpuFacts facts)
    {
        var entries = new HardwareNative.ProcessorPowerInformation[Math.Max(1, facts.LogicalProcessors)];
        fixed (HardwareNative.ProcessorPowerInformation* first = entries)
        {
            var bytes = (uint)(entries.Length * sizeof(HardwareNative.ProcessorPowerInformation));
            if (HardwareNative.CallNtPowerInformation(HardwareNative.ProcessorInformationLevel, null, 0, first, bytes) != 0)
            {
                return facts;
            }
        }

        return facts with
        {
            MaximumMegahertz = (int)entries[0].MaximumMegahertz,
            LimitMegahertz = (int)entries[0].LimitMegahertz,
            BaseMegahertz = facts.BaseMegahertz == 0 ? (int)entries[0].MaximumMegahertz : facts.BaseMegahertz,
        };
    }
}
