using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace Gapo.Sandbox
{
    static class Program
    {
        const int ExitBadArguments = 2;
        const int ExitFailed = 3;
        const string FailureFileName = "sandbox-error.txt";

        [STAThread]
        static int Main(string[] args)
        {
            FixtureOptions options;
            try
            {
                options = FixtureOptions.Parse(args);
            }
            catch (ArgumentException exception)
            {
                ReportFailure(null, exception.Message);
                return ExitBadArguments;
            }

            try
            {
                if (options.ResetSettings) File.Delete(options.Paths.SettingsFile);
                if (options.Recalibrate) File.Delete(options.Paths.CalibrationFile);
                if (options.BelowNormalPriority)
                {
                    using (var self = Process.GetCurrentProcess()) self.PriorityClass = ProcessPriorityClass.BelowNormal;
                }

                Native.timeBeginPeriod(1);
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new GameForm(options))
                    {
                        Application.Run(form);
                        return form.ExitCode;
                    }
                }
                finally
                {
                    Native.timeEndPeriod(1);
                }
            }
            catch (Exception exception)
            {
                ReportFailure(options, exception.ToString());
                return ExitFailed;
            }
        }

        public static void ReportFailure(FixtureOptions options, string message)
        {
            try
            {
                string path = options != null && !string.IsNullOrEmpty(options.LogPath)
                    ? options.LogPath + ".error.txt"
                    : Path.Combine(Path.GetTempPath(), FailureFileName);
                File.WriteAllText(path, message);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
