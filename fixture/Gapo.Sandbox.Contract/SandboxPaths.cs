namespace Gapo.Sandbox.Contract;

public sealed class SandboxPaths
{
    public const string DataDirectoryName = "GAPO-Sandbox";

    public SandboxPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public static SandboxPaths Default { get; } = new(DefaultDataDirectory());

    public string DataDirectory { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "sandbox-settings.ini");

    public string CalibrationFile => Path.Combine(DataDirectory, "calibration.ini");

    public string SaveFile => Path.Combine(DataDirectory, "world.sav");

    private static string DefaultDataDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var parent = string.IsNullOrEmpty(localApplicationData) ? Path.GetTempPath() : localApplicationData;
        return Path.Combine(parent, DataDirectoryName);
    }
}
