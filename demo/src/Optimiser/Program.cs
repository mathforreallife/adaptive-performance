using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Gapo.Optimiser.Engine;
using Gapo.Optimiser.Game;
using Gapo.Optimiser.Hardware;
using Gapo.Optimiser.Monitoring;
using Gapo.Optimiser.UI;

namespace Gapo.Optimiser
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string autotest = null; bool selftest = false; Goal goal = Goal.Balanced;
            foreach (var a in args)
            {
                if (a == "--selftest") selftest = true;
                else if (a.StartsWith("--autotest=")) autotest = a.Substring(11);
                else if (a.StartsWith("--goal=")) Enum.TryParse(a.Substring(7), true, out goal);
            }
            if (selftest) return SelfTest(goal);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(autotest, goal));
            return 0;
        }

        /// <summary>Console run of the full engine against a running sandbox (no UI). Used for testing.</summary>
        static int SelfTest(Goal goal)
        {
            Console.WriteLine("GAPO self-test: scanning hardware…");
            var hw = HardwareScanner.Scan(null, s => Console.WriteLine("  scan: " + s));
            foreach (var l in OptimisationReport.HardwareLines(hw)) Console.WriteLine("  " + l);
            foreach (var n in hw.Notes) Console.WriteLine("  note: " + n);
            Console.WriteLine("  scan took " + hw.ScanMs.ToString("0") + " ms");
            using (var monitor = new SystemMonitor())
            using (var link = new GameLink())
            {
                var t0 = DateTime.Now;
                while (!link.Connected && (DateTime.Now - t0).TotalSeconds < 60) Thread.Sleep(200);
                if (!link.Connected) { Console.WriteLine("No game found."); return 2; }
                monitor.SetGame(link.Pid);
                Console.WriteLine("Connected to " + link.Title + " (pid " + link.Pid + "), settings: " + link.SettingsPath);
                var engine = new OptimisationEngine(link, monitor, hw);
                string last = "";
                engine.Changed += s =>
                {
                    string line = "[" + s.Phase + " " + (s.Progress * 100).ToString("0") + "%] " + s.Title;
                    if (line != last) { Console.WriteLine(line); last = line; }
                };
                engine.Start(goal);
                while (engine.Running) Thread.Sleep(200);
                var rep = engine.Report;
                Console.WriteLine();
                Console.WriteLine(rep.ToText());
                return rep.Completed ? 0 : 1;
            }
        }
    }
}
