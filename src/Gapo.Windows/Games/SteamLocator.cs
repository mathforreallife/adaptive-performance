using Microsoft.Win32;

namespace Gapo.Windows.Games;

public static class SteamLocator
{
    private const string UserKey = @"Software\Valve\Steam";
    private const string UserValue = "SteamPath";
    private const string MachineKey = @"SOFTWARE\Valve\Steam";
    private const string MachineValue = "InstallPath";

    public static string? FindSteamRoot()
    {
        using var user = Registry.CurrentUser.OpenSubKey(UserKey);
        if (Existing(user?.GetValue(UserValue) as string) is { } fromUser)
        {
            return fromUser;
        }

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var machine = hive.OpenSubKey(MachineKey);
            if (Existing(machine?.GetValue(MachineValue) as string) is { } fromMachine)
            {
                return fromMachine;
            }
        }

        return null;
    }

    private static string? Existing(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var full = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(full) ? full : null;
    }
}
