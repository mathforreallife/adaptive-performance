using System.Diagnostics;
using System.Globalization;
using Gapo.App.Overlay;
using Gapo.App.Preferences;
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
    private const int NotificationSettleMilliseconds = 1500;
    private const string RestartRequestPrefix = "Restart the game to test";

    private static readonly TimeSpan OwnUseSpan = TimeSpan.FromSeconds(30);
    private static readonly OverheadBudget WindowBudget = OverheadBudget.For(OptimiserState.DashboardOpen);

    private readonly OwnProcessProbe ownProbe = new();
    private readonly OverheadMeter ownMeter = new(Environment.ProcessorCount, OwnUseSpan);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan ownMeterStarted = TimeSpan.MaxValue;
    private readonly AppOptions options;
    private readonly HomeView home = new();
    private readonly AgentLink? link;
    private readonly Notifier? notifier;
    private readonly GameSettingsView gameSettings = new();
    private readonly SettingsView settingsView = new();
    private readonly PreferenceStore preferences;
    private readonly OverlayController? overlay;
    private GameSettingsMessage? latestSettings;
    private bool windowIsActive = true;
    private ReportView report = ReportView.Empty;
    private bool rendersLiveData = true;
    private int samplesRendered;

    public MainWindow(AppOptions options)
    {
        this.options = options;
        InitializeComponent();
        Title = "GAPO";
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(DefaultWidth, DefaultHeight));
        preferences = new PreferenceStore(options.PreferencesPath ?? PreferenceStore.DefaultPath);
        Root.RequestedTheme = options.Theme != ElementTheme.Default ? options.Theme : preferences.Current.Theme;
        settingsView.BackRequested += ShowHome;
        settingsView.PreferencesChanged += ApplyPreferences;
        Root.Loaded += OnLoaded;
        Activated += (_, args) =>
        {
            windowIsActive = args.WindowActivationState != WindowActivationState.Deactivated;
            rendersLiveData = windowIsActive || options.ScreenshotDirectory is not null;
        };
        Closed += (_, _) =>
        {
            link?.Dispose();
            notifier?.Dispose();
            overlay?.Dispose();
        };
        home.OptimiseRequested += goal => link?.Send(new OptimiseCommand(goal));
        home.PlanRequested += goal => link?.Send(new PlanCommand(goal));
        home.DiagnoseRequested += () => link?.Send(new DiagnoseCommand());
        home.StopRequested += () => link?.Send(new StopCommand());
        home.RevertAllRequested += () => link?.Send(new RevertAllCommand());
        home.GameSettingsRequested += ShowGameSettings;
        home.ProposalAnswered += approved => link?.Send(new AnswerProposalCommand(approved));
        gameSettings.BackRequested += ShowHome;
        gameSettings.ValueChosen += (key, value) => link?.Send(new SetSettingCommand(key, value));
        gameSettings.LockChanged += (key, locked) => link?.Send(new LockSettingCommand(key, locked));
        gameSettings.PutBackRequested += key => link?.Send(new RevertSettingCommand(key));
        if (options.ReportPath is { } path && File.Exists(path))
        {
            ShowReport(path);
        }
        else
        {
            ShowHome();
            if (options.ScreenshotDirectory is null && options.NotificationTestPath is null)
            {
                notifier = new Notifier(DispatcherQueue);
                notifier.Invoked += BringToFront;
            }

            overlay = new OverlayController();
            ApplyPreferences(preferences.Current);
            link = new AgentLink(options.PipeName, DispatcherQueue);
            link.MessageReceived += OnAgentMessage;
            link.ConnectionChanged += connected =>
            {
                StatusText.Text = connected ? "Connected to the GAPO agent" : "Waiting for the GAPO agent";
                if (connected)
                {
                    SendAgentPreferences(preferences.Current);
                }
            };
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

    public void ShowSettings()
    {
        settingsView.Show(preferences.Current, overlay?.Status ?? "The overlay is available while GAPO is connected to its agent.");
        Show(settingsView, "Settings");
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => ShowSettings();

    private void ApplyPreferences(AppPreferences chosen)
    {
        if (!ReferenceEquals(chosen, preferences.Current))
        {
            preferences.Save(chosen);
        }

        if (options.Theme == ElementTheme.Default)
        {
            Root.RequestedTheme = chosen.Theme;
        }

        SendAgentPreferences(chosen);
        home.ShowOptimiserChoices(chosen.OptimiserEnabled, chosen.DiagnoseAutomatically);
        if (overlay is not null)
        {
            overlay.Enabled = chosen.OverlayEnabled;
            overlay.Corner = chosen.OverlayCorner;
            overlay.ShowsBeforeAndAfter = chosen.OverlayShowsBeforeAndAfter;
            if (!chosen.OverlayEnabled)
            {
                overlay.Hide();
            }
        }
    }

    public bool HasGameSettings => latestSettings is not null;

    public void ShowGameSettings()
    {
        if (latestSettings is null)
        {
            return;
        }

        gameSettings.Show(latestSettings);
        Show(gameSettings, "Game settings");
    }

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
                NotifyWhenAway(status);
                break;
            case LiveSampleMessage sample:
                MeasureOwnUse();
                if (overlay is not null)
                {
                    overlay.Update(sample);
                    if (ReferenceEquals(Body.Content, settingsView))
                    {
                        settingsView.ShowOverlayState(overlay.Status);
                    }
                }

                if (!sample.GameConnected && latestSettings is not null)
                {
                    latestSettings = null;
                    home.ShowGameSettingsAvailable(false);
                    if (ReferenceEquals(Body.Content, gameSettings))
                    {
                        ShowHome();
                    }
                }

                if (rendersLiveData)
                {
                    ShowOwnUse(sample);
                    home.ShowSample(sample);
                    samplesRendered += sample.GameConnected ? 1 : 0;
                }

                break;
            case PlanMessage plan:
                home.ShowPlan(plan);
                break;
            case ProgressMessage progress:
                home.ShowProgress(progress);
                break;
            case ProposalMessage proposal:
                home.ShowProposal(proposal);
                NotifyWhenAway(new StatusMessage(AgentState.Optimising, proposal.Title, "GAPO waits for your answer before it changes anything."), always: true);
                break;
            case GameSettingsMessage settings:
                latestSettings = settings;
                home.ShowGameSettingsAvailable(true);
                if (ReferenceEquals(Body.Content, gameSettings))
                {
                    gameSettings.Show(settings);
                }

                break;
            case DiagnosisMessage diagnosis:
                home.ShowDiagnosis(diagnosis);
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
        if (link is null || link.IsConnected || options.AgentPath is null || !preferences.Current.StartAgentAutomatically)
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

    private void SendAgentPreferences(AppPreferences chosen) =>
        link?.Send(new AgentPreferencesCommand(chosen.AskBeforeEachChange, chosen.AllowFrameCap, chosen.TargetFps, chosen.MostRestarts, chosen.OptimiserEnabled, chosen.DiagnoseAutomatically));

    private void NotifyWhenAway(StatusMessage status, bool always = false)
    {
        var worthInterrupting = always || status.Title.StartsWith(RestartRequestPrefix, StringComparison.Ordinal) || status.State is AgentState.Finished or AgentState.Failed;
        if (windowIsActive || !worthInterrupting || options.ScreenshotDirectory is not null)
        {
            return;
        }

        if (preferences.Current.FlashTaskbarWhenAway)
        {
            TaskbarFlash.UntilActivated(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }

        if (preferences.Current.NotifyWhenAway)
        {
            notifier?.Show(status.Title, FirstSentence(status.Detail));
        }
    }

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 1)];
    }

    private void BringToFront()
    {
        AppWindow.Show();
        Activate();
    }

    private async Task RunNotificationTestAsync(string resultPath)
    {
        await File.WriteAllTextAsync(resultPath, "registering;");
        using var test = new Notifier(DispatcherQueue);
        await File.AppendAllTextAsync(resultPath, string.Create(CultureInfo.InvariantCulture, $"available={test.IsAvailable};"));
        var shown = test.Show("GAPO notification test", "If you can read this, GAPO can tell you when to restart a game.");
        await File.AppendAllTextAsync(resultPath, string.Create(CultureInfo.InvariantCulture, $"shown={shown};"));
        await Task.Delay(NotificationSettleMilliseconds);
        var inActionCentre = await test.CountShownAsync();
        await File.AppendAllTextAsync(resultPath, string.Create(CultureInfo.InvariantCulture, $"inActionCentre={inActionCentre};"));
        await test.RemoveAllAsync();
        await File.AppendAllTextAsync(resultPath, "removed");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (options.NotificationTestPath is { } resultPath)
        {
            await RunNotificationTestAsync(resultPath);
            Close();
            return;
        }

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
