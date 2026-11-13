using System.Diagnostics;
using System.Globalization;
using Gapo.App.Reports;
using Gapo.App.Views;
using Gapo.Core.Overhead;
using Gapo.Core.Protocol;
using Gapo.Windows.Processes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gapo.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1040;
    private const int DefaultHeight = 720;
    private const int StartAgentAfterMilliseconds = 3000;
    private const int SamplesBeforeLiveScreenshot = 4;

    private static readonly TimeSpan OwnUseSpan = TimeSpan.FromSeconds(30);
    private static readonly OverheadBudget WindowBudget = OverheadBudget.For(OptimiserState.DashboardOpen);

    private readonly OwnProcessProbe ownProbe = new();
    private readonly OverheadMeter ownMeter = new(Environment.ProcessorCount, OwnUseSpan);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan ownMeterStarted = TimeSpan.MaxValue;
    private readonly AppOptions options;
    private readonly HomeView home = new();
    private readonly AgentLink? link;
    private ReportView report = ReportView.Empty;
    private bool rendersLiveData = true;
    private int samplesRendered;

    public MainWindow(AppOptions options)
    {
        this.options = options;
        InitializeComponent();
        Title = "GAPO";
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(DefaultWidth, DefaultHeight));
        Root.RequestedTheme = options.Theme;
        Root.Loaded += OnLoaded;
        Activated += (_, args) => rendersLiveData = args.WindowActivationState != WindowActivationState.Deactivated || options.ScreenshotDirectory is not null;
        Closed += (_, _) => link?.Dispose();
        home.OptimiseRequested += goal => link?.Send(new OptimiseCommand(goal));
        home.StopRequested += () => link?.Send(new StopCommand());
        home.RevertAllRequested += () => link?.Send(new RevertAllCommand());
        if (options.ReportPath is { } path && File.Exists(path))
        {
            ShowReport(path);
        }
        else
        {
            ShowHome();
            link = new AgentLink(options.PipeName, DispatcherQueue);
            link.MessageReceived += OnAgentMessage;
            link.ConnectionChanged += connected => StatusText.Text = connected ? "Connected to the GAPO agent" : "Waiting for the GAPO agent";
            StatusText.Text = "Waiting for the GAPO agent";
            DispatcherQueue.TryEnqueue(StartAgentIfNeededAsync);
        }
    }

    public void ShowResults()
    {
        var view = new ResultsView(report, canAct: link is not null);
        view.MoreOptionsRequested += (_, _) => ShowDetails();
        view.OptimiseAgainRequested += (_, _) => ShowHome();
        view.RevertAllRequested += (_, _) =>
        {
            link?.Send(new RevertAllCommand());
            ShowHome();
        };
        Show(view, report.HasResult ? "Optimisation finished" : "No result yet");
    }

    public void ShowDetails(int tab = 0)
    {
        var view = new DetailsView(report, tab);
        view.BackRequested += (_, _) => ShowResults();
        Show(view, "All measurements and reasons");
    }

    private void ShowHome() => Show(home, StatusText.Text);

    private void ShowReport(string path)
    {
        report = ReportView.Load(path);
        OverheadText.Text = report.OverheadText;
        SetOverheadWarning(report.OverBudget);
        if (report.HasResult)
        {
            ShowResults();
        }
        else
        {
            ShowHome();
        }
    }

    private void OnAgentMessage(AgentMessage message)
    {
        switch (message)
        {
            case StatusMessage status:
                StatusText.Text = status.Title;
                home.ShowStatus(status);
                break;
            case LiveSampleMessage sample:
                MeasureOwnUse();
                if (rendersLiveData)
                {
                    ShowOwnUse(sample);
                    home.ShowSample(sample);
                    samplesRendered += sample.GameConnected ? 1 : 0;
                }

                break;
            case ReportReadyMessage ready when File.Exists(ready.Path):
                ShowReport(ready.Path);
                break;
        }
    }

    private void MeasureOwnUse()
    {
        var now = clock.Elapsed;
        ownMeter.Record(now, ownProbe.Read(), insideMeasurementWindow: false);
        if (ownMeterStarted == TimeSpan.MaxValue)
        {
            ownMeterStarted = now;
        }
    }

    private void ShowOwnUse(LiveSampleMessage sample)
    {
        var windowCpu = ownMeter.RollingCpuPercent;
        var windowMegabytes = ownMeter.PrivateMegabytes;
        var judgedLongEnough = clock.Elapsed - ownMeterStarted >= OwnUseSpan;
        var windowIsOverBudget = (judgedLongEnough && windowCpu > WindowBudget.MaximumCpuPercent) || windowMegabytes > WindowBudget.MaximumPrivateMegabytes;
        OverheadText.Text = string.Create(CultureInfo.InvariantCulture, $"GAPO itself: {sample.OwnCpuPercent + windowCpu:0.00} % CPU, {sample.OwnMegabytes + windowMegabytes:0} MB");
        ToolTipService.SetToolTip(OverheadText, string.Create(CultureInfo.InvariantCulture, $"Background agent: {sample.OwnCpuPercent:0.00} % CPU, {sample.OwnMegabytes:0} MB. This window: {windowCpu:0.00} % CPU, {windowMegabytes:0} MB."));
        SetOverheadWarning(sample.OwnUseIsOverBudget || windowIsOverBudget);
    }

    private void SetOverheadWarning(bool overBudget) =>
        OverheadText.Foreground = (Brush)Application.Current.Resources[overBudget ? "GapoWarnBrush" : "GapoTextSecondaryBrush"];

    private void Show(UIElement view, string status)
    {
        Body.Content = view;
        StatusText.Text = status;
        BodyScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private async void StartAgentIfNeededAsync()
    {
        await Task.Delay(StartAgentAfterMilliseconds);
        if (link is null || link.IsConnected || options.AgentPath is null)
        {
            return;
        }

        var start = new ProcessStartInfo(options.AgentPath) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--serve");
        start.ArgumentList.Add("--pipe-name=" + options.PipeName);
        start.ArgumentList.Add("--exit-with-process=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "The GAPO agent could not be started";
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (options.ScreenshotDirectory is not { } directory)
        {
            return;
        }

        if (link is not null)
        {
            while (samplesRendered < SamplesBeforeLiveScreenshot)
            {
                await Task.Delay(500);
            }

            var remaining = TimeSpan.FromSeconds(options.ScreenshotAfterSeconds) - clock.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }
        }

        await new ScreenshotRun(this, Root, directory, report.HasResult).CaptureAllAsync();
        Close();
    }
}
