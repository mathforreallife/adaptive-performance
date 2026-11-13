using System.Globalization;
using Gapo.Core.Protocol;

namespace Gapo.App.Overlay;

public sealed class OverlayController : IDisposable
{
    private static readonly TimeSpan WindowSearchInterval = TimeSpan.FromSeconds(5);

    private readonly OverlayWindow window = new();
    private int processId;
    private nint gameWindow;
    private DateTime nextSearch = DateTime.MinValue;

    public bool Enabled { get; set; } = true;

    public bool ShowsBeforeAndAfter { get; set; } = true;

    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;

    public string Status { get; private set; } = "The overlay appears when a game is running.";

    public static string[] TextFor(LiveSampleMessage sample, bool showsBeforeAndAfter)
    {
        var live = string.Create(CultureInfo.InvariantCulture, $"GAPO  {sample.AverageFps:0} FPS");
        if (!showsBeforeAndAfter)
        {
            return [live];
        }

        return sample.BeforeFps > 0 && sample.AfterFps > 0
            ? [live, string.Create(CultureInfo.InvariantCulture, $"before {sample.BeforeFps:0}  ·  after {sample.AfterFps:0} FPS")]
            : [live, "not optimised yet"];
    }

    public void Update(LiveSampleMessage sample)
    {
        if (!Enabled || !sample.GameConnected || sample.GameProcessId == 0 || sample.AverageFps <= 0)
        {
            window.Hide();
            Status = Enabled ? "The overlay appears when a game is running." : "The overlay is switched off.";
            return;
        }

        if (sample.GameProcessId != processId || gameWindow == 0 || DateTime.UtcNow >= nextSearch)
        {
            processId = sample.GameProcessId;
            gameWindow = GameWindowLocator.MainWindowOf(processId);
            nextSearch = DateTime.UtcNow + WindowSearchInterval;
        }

        if (GameWindowLocator.AnExclusiveFullScreenGameIsRunning())
        {
            window.Hide();
            Status = "The game runs in exclusive fullscreen, where only an overlay inside the game could appear. GAPO never injects into games; switch the game to borderless or windowed to see the overlay.";
            return;
        }

        if (!GameWindowLocator.IsInFront(gameWindow) || !GameWindowLocator.TryGetBounds(gameWindow, out var bounds))
        {
            window.Hide();
            Status = "The overlay shows while the game window is in front.";
            return;
        }

        window.Show(TextFor(sample, ShowsBeforeAndAfter), bounds, Corner);
        Status = "The overlay is showing over the game.";
    }

    public void Hide() => window.Hide();

    public void Dispose() => window.Dispose();
}
