using Gapo.Core.Hardware;
using Microsoft.Win32;

namespace Gapo.Windows.Hardware;

internal static unsafe class GraphicsProbe
{
    private const string DisplayAdapterClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const int AdapterSubkeyNameLength = 4;
    private const int ReleaseSlot = 2;
    private const int EnumerateAdaptersSlot = 12;
    private const int DescribeAdapterSlot = 10;
    private const uint SoftwareAdapterFlag = 2;
    private const int MaximumAdapters = 16;

    private static readonly Guid FactoryInterface = new("770aae78-f26f-4dba-a829-253c83d1b387");

    internal static IReadOnlyList<GpuFacts> Read()
    {
        var drivers = ReadDriverDetails();
        var adapters = new List<GpuFacts>();
        if (HardwareNative.CreateDXGIFactory1(FactoryInterface, out var factory) < 0 || factory == 0)
        {
            throw new InvalidOperationException("The graphics factory could not be created.");
        }

        try
        {
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)VirtualMethod(factory, EnumerateAdaptersSlot);
            for (uint index = 0; index < MaximumAdapters; index++)
            {
                nint adapter;
                if (enumerate(factory, index, &adapter) < 0 || adapter == 0)
                {
                    break;
                }

                try
                {
                    var facts = Describe(adapter, drivers);
                    if (facts is not null && !adapters.Any(known => known.Name == facts.Name && known.DeviceId == facts.DeviceId))
                    {
                        adapters.Add(facts);
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return adapters;
    }

    private static GpuFacts? Describe(nint adapter, Dictionary<string, (string Version, string Date)> drivers)
    {
        var description = default(HardwareNative.AdapterDescription);
        var describe = (delegate* unmanaged[Stdcall]<nint, HardwareNative.AdapterDescription*, int>)VirtualMethod(adapter, DescribeAdapterSlot);
        if (describe(adapter, &description) < 0)
        {
            return null;
        }

        var name = new string(description.Name).Trim();
        var dedicatedBytes = (long)description.DedicatedVideoMemory;
        drivers.TryGetValue(name, out var driver);
        return new GpuFacts
        {
            Name = name,
            Vendor = HardwareRules.VendorName(description.VendorId),
            VendorId = description.VendorId,
            DeviceId = description.DeviceId,
            DedicatedVideoMemoryBytes = dedicatedBytes,
            SharedMemoryBytes = (long)description.SharedSystemMemory,
            DriverVersion = driver.Version ?? string.Empty,
            DriverDate = driver.Date ?? string.Empty,
            Kind = HardwareRules.ClassifyGpu(name, description.VendorId, dedicatedBytes, (description.Flags & SoftwareAdapterFlag) != 0),
        };
    }

    private static Dictionary<string, (string Version, string Date)> ReadDriverDetails()
    {
        var drivers = new Dictionary<string, (string Version, string Date)>(StringComparer.OrdinalIgnoreCase);
        using var adapterClass = Registry.LocalMachine.OpenSubKey(DisplayAdapterClassKey);
        if (adapterClass is null)
        {
            return drivers;
        }

        foreach (var subkeyName in adapterClass.GetSubKeyNames().Where(name => name.Length == AdapterSubkeyNameLength))
        {
            try
            {
                using var adapter = adapterClass.OpenSubKey(subkeyName);
                if (adapter?.GetValue("DriverDesc") is string description && description.Length > 0)
                {
                    drivers[description] = (adapter.GetValue("DriverVersion") as string ?? string.Empty, adapter.GetValue("DriverDate") as string ?? string.Empty);
                }
            }
            catch (System.Security.SecurityException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return drivers;
    }

    private static void* VirtualMethod(nint comObject, int slot) => (*(void***)comObject)[slot];

    private static void Release(nint comObject) => ((delegate* unmanaged[Stdcall]<nint, uint>)VirtualMethod(comObject, ReleaseSlot))(comObject);
}
