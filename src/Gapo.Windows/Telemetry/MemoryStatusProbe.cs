using System.Runtime.InteropServices;
using Gapo.Core.Telemetry;
using Gapo.Windows.Processes;

namespace Gapo.Windows.Telemetry;

public static class MemoryStatusProbe
{
    public static MemoryStatus Read()
    {
        var status = new ProcessNative.MemoryStatusEx { Length = (uint)Marshal.SizeOf<ProcessNative.MemoryStatusEx>() };
        if (!ProcessNative.GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException($"GlobalMemoryStatusEx failed with error {Marshal.GetLastPInvokeError()}.");
        }

        return new MemoryStatus(
            (long)status.TotalPhysical,
            (long)status.AvailablePhysical,
            (long)status.TotalPageFile,
            (long)(status.TotalPageFile - status.AvailablePageFile),
            (int)status.MemoryLoad);
    }
}
