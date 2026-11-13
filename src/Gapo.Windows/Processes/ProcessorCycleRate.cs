using Microsoft.Win32;

namespace Gapo.Windows.Processes;

public static class ProcessorCycleRate
{
    private const string FirstProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string NominalMegahertzValue = "~MHz";
    private const double CyclesPerMegahertzSecond = 1_000_000.0;

    public static double? ReadNominalCyclesPerSecond()
    {
        using var key = Registry.LocalMachine.OpenSubKey(FirstProcessorKey);
        return key?.GetValue(NominalMegahertzValue) is int megahertz && megahertz > 0
            ? megahertz * CyclesPerMegahertzSecond
            : null;
    }
}
