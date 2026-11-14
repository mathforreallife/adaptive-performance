using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gapo.App;

public sealed class ScreenshotRun
{
    private const int LayoutSettleMilliseconds = 400;
    private const int DetailTabs = 5;
    private const double DefaultDotsPerInch = 96;
    private const int PlanWaitSteps = 25;
    private const int PlanWaitMilliseconds = 200;

    private readonly MainWindow window;
    private readonly FrameworkElement root;
    private readonly string directory;
    private readonly bool hasResult;

    public ScreenshotRun(MainWindow window, FrameworkElement root, string directory, bool hasResult)
    {
        this.window = window;
        this.root = root;
        this.directory = directory;
        this.hasResult = hasResult;
    }

    public async Task CaptureAllAsync()
    {
        Directory.CreateDirectory(directory);
        try
        {
            if (!hasResult)
            {
                await CaptureAsync("home");
                if (window.RequestPlan())
                {
                    for (var step = 0; step < PlanWaitSteps && !window.HasPlan; step++)
                    {
                        await Task.Delay(PlanWaitMilliseconds);
                    }

                    if (window.HasPlan)
                    {
                        await CaptureAsync("plan");
                    }
                }

                if (window.HasGameSettings)
                {
                    window.ShowGameSettings();
                    await CaptureAsync("game-settings");
                }

                window.ShowSettings();
                await CaptureAsync("settings");

                return;
            }

            window.ShowResults();
            await CaptureAsync("results");
            for (var tab = 0; tab < DetailTabs; tab++)
            {
                window.ShowDetails(tab);
                await CaptureAsync("details-" + (tab + 1));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "screenshot-error.txt"), exception.ToString());
        }
    }

    private async Task CaptureAsync(string name)
    {
        await Task.Delay(LayoutSettleMilliseconds);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, DefaultDotsPerInch, DefaultDotsPerInch, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(Path.Combine(directory, name + ".png"), bytes);
    }
}
