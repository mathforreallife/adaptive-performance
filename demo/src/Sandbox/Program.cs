using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Gapo.Shared;

namespace Gapo.Sandbox
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            double autotest = 0; string shot = null, log = null; double headless = 0;
            foreach (var a in args)
            {
                if (a.StartsWith("--autotest=")) autotest = double.Parse(a.Substring(11), CultureInfo.InvariantCulture);
                else if (a.StartsWith("--screenshot=")) shot = a.Substring(13);
                else if (a.StartsWith("--log=")) log = a.Substring(6);
                else if (a.StartsWith("--headless=")) headless = double.Parse(a.Substring(11), CultureInfo.InvariantCulture);
                else if (a == "--reset") { try { File.Delete(DemoPaths.SettingsFile); } catch { } }
                else if (a == "--recalibrate") Game.DeleteCalibration();
            }
            if (headless > 0) return Headless.Run(headless, shot, log);

            Native.EnableDpiAwareness();
            if (Native.IsWindows) Native.timeBeginPeriod(1);
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new GameForm(autotest, shot, log));
            }
            finally { if (Native.IsWindows) Native.timeEndPeriod(1); }
            return 0;
        }
    }

    /// <summary>Runs the game loop without a window (used for testing off Windows).</summary>
    static class Headless
    {
        public static int Run(double seconds, string shot, string log)
        {
            using (var game = new Game())
            {
                game.Init(m => Console.WriteLine(m));
                Console.WriteLine("Calibrated: base {0}x{1}, {2} creatures, Ultra ≈ {3:0.0} ms", game.BaseW, game.BaseH, game.Sim.Total, game.CalibratedMs);
                var input = new InputState();
                var sw = Stopwatch.StartNew();
                double nextPrint = 1000;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    var r = game.Frame(input, null);
                    if (sw.Elapsed.TotalMilliseconds > nextPrint)
                    {
                        nextPrint += 1000;
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "t={0:0}s fps={1:0.0} work={2:0.0} sim={3:0.0} (ent {4:0.0} phys {5:0.0}) render={6:0.0} [terrain {7:0.0} sky {8:0.0} shadow {9:0.0} water {10:0.0} fog {11:0.0} sprites {12:0.0} post {13:0.0}] io={14:0.0} rev={15} thr={16} flags={17}",
                            sw.Elapsed.TotalSeconds, game.Fps, r.WorkMs, r.SimMs, r.SimEntitiesMs, r.SimPhysicsMs, r.RenderMs, r.TerrainMs, r.SkyMs, r.ShadowMs, r.WaterMs, r.FogMs, r.SpritesMs, r.PostMs, r.IoMs, r.Revision, r.RenderThreads, (FrameFlags)r.Flags));
                    }
                }
                if (shot != null) SaveShot(game, shot);
            }
            return 0;
        }

        static void SaveShot(Game game, string path)
        {
            var r = game.Renderer;
            using (var bmp = new System.Drawing.Bitmap(r.OutW, r.OutH, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, r.OutW, r.OutH), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                for (int y = 0; y < r.OutH; y++)
                    System.Runtime.InteropServices.Marshal.Copy(r.Output, y * r.OutW, data.Scan0 + y * data.Stride, r.OutW);
                bmp.UnlockBits(data);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
