using System.Globalization;
using Gapo.Core.Optimisation;
using Gapo.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Gapo.App.Views;

public sealed partial class HomeView : UserControl
{
    private const double SixtyFpsFrameMs = 1000.0 / 60;
    private const double ThirtyFpsFrameMs = 1000.0 / 30;
    private const double SmallestChartTopMs = 40;
    private const double ChartHeadroom = 1.15;
    private const string NoValue = "–";

    private static readonly Goal[] Goals = [Goal.Balanced, Goal.MaximumFps, Goal.Quality, Goal.Smoothness];

    private IReadOnlyList<double> chartValues = [];
    private bool optimising;
    private bool canOptimise = true;
    private string shownGame = string.Empty;
    private bool optimiserEnabled = true;
    private bool diagnoseAutomatically = true;
    private bool gameConnected;
    private bool planShowing;
    private Goal plannedGoal = Goal.Balanced;
    private PlanMessage? plan;
    private const int SecondsPerMinute = 60;

    public HomeView()
    {
        InitializeComponent();
        GoalSelector.SelectedItem = GoalSelector.Items[0];
    }

    public event Action<Goal>? OptimiseRequested;

    public event Action? StopRequested;

    public event Action? RevertAllRequested;

    public event Action? GameSettingsRequested;

    public event Action<bool>? ProposalAnswered;

    public event Action<Goal>? PlanRequested;

    public event Action? DiagnoseRequested;

    public void ShowOptimiserChoices(bool enabled, bool diagnosesAutomatically)
    {
        optimiserEnabled = enabled;
        diagnoseAutomatically = diagnosesAutomatically;
        if (!enabled)
        {
            HidePlan();
        }

        UpdateControls(gameConnected);
    }

    public void ShowPlan(PlanMessage received)
    {
        plan = received;
        planShowing = true;
        PlanTitle.Text = received.CanStart ? "Plan for " + received.GameName : received.GameName.Length > 0 ? received.GameName + " cannot be optimised now" : "Nothing to optimise";
        PlanSummary.Text = received.Summary;
        PlanSteps.Text = string.Join(Environment.NewLine, received.Steps.Select((step, index) => string.Create(CultureInfo.InvariantCulture, $"{index + 1}. {step}")));
        PlanSteps.Visibility = received.Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = received.CanStart ? Visibility.Visible : Visibility.Collapsed;
        CancelPlanButton.Content = received.CanStart ? "Cancel" : "Close";
        PlanPanel.Visibility = Visibility.Visible;
        UpdateControls(gameConnected);
    }

    public void ShowProgress(ProgressMessage progress)
    {
        if (!optimising)
        {
            return;
        }

        ProgressPanel.Visibility = Visibility.Visible;
        ProgressPhase.Text = progress.Phase;
        var known = progress.StepsExpected > 0;
        ProgressMeter.IsIndeterminate = !known;
        ProgressMeter.Value = known ? Math.Clamp((double)progress.StepsDone / progress.StepsExpected, 0, 1) : 0;
        var left = RemainingText(progress);
        ProgressLine.Text = known
            ? string.Create(CultureInfo.InvariantCulture, $"Step {progress.StepsDone} of about {progress.StepsExpected} · {Clock(progress.ElapsedSeconds)} so far · {left}")
            : string.Create(CultureInfo.InvariantCulture, $"Getting ready · {Clock(progress.ElapsedSeconds)} so far · {left}");
    }

    public void ShowProposal(ProposalMessage proposal)
    {
        ProposalTitle.Text = proposal.Title;
        ProposalDetail.Text = proposal.Detail;
        ProposalPanel.Visibility = Visibility.Visible;
    }

    public void ShowGameSettingsAvailable(bool available) =>
        GameSettingsButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

    public Goal SelectedGoal => Goals[Math.Max(0, GoalSelector.Items.IndexOf(GoalSelector.SelectedItem))];

    public void ShowStatus(StatusMessage status)
    {
        optimising = status.State == AgentState.Optimising;
        if (optimising)
        {
            HidePlan();
        }
        else
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
        }

        if (status.State is AgentState.NoGame or AgentState.Optimising or AgentState.Failed)
        {
            GameTitle.Text = status.Title;
        }

        StatusDetail.Text = status.Detail;
        UpdateControls(status.State != AgentState.NoGame);
    }

    public void ShowSample(LiveSampleMessage sample)
    {
        LivePanel.Visibility = sample.GameConnected ? Visibility.Visible : Visibility.Collapsed;
        canOptimise = sample.CanOptimise;
        gameConnected = sample.GameConnected;
        if (!sample.GameConnected && planShowing)
        {
            HidePlan();
        }
        UpdateControls(sample.GameConnected);
        if (sample.GameName != shownGame)
        {
            shownGame = sample.GameName;
            DiagnosisPanel.Visibility = Visibility.Collapsed;
        }

        if (!sample.GameConnected)
        {
            if (!optimising)
            {
                GameTitle.Text = "No game is running";
            }

            return;
        }

        if (!optimising)
        {
            GameTitle.Text = sample.GameName;
        }

        FpsValue.Text = Number(sample.AverageFps, "0");
        FrameTimeValue.Text = Number(sample.MedianFrameMs, "0.0");
        FrameWorkValue.Text = Number(sample.MedianBusyMs, "0.0");
        StutterValue.Text = sample.AverageFps > 0 ? sample.StuttersPerMinute.ToString("0", CultureInfo.InvariantCulture) : NoValue;
        LoadLine.Text = string.Create(CultureInfo.InvariantCulture, $"The game uses {sample.GameProcessorsUsed:0.0} processors; the whole PC is at {sample.MachineLoadPercent:0} % CPU.");
        chartValues = sample.RecentFrameMs;
        DrawChart();
    }

    public void ShowDiagnosis(DiagnosisMessage diagnosis)
    {
        DiagnosisTitle.Text = diagnosis.Title;
        DiagnosisSummary.Text = diagnosis.Summary;
        DiagnosisAlso.Text = string.Join(Environment.NewLine, diagnosis.AlsoNoticed.Select(line => "Also: " + line));
        DiagnosisAlso.Visibility = diagnosis.AlsoNoticed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiagnosisBasis.Text = diagnosis.Basis;
        shownGame = diagnosis.GameName;
        DiagnosisPanel.Visibility = Visibility.Visible;
        UpdateControls(gameConnected);
    }

    private void UpdateControls(bool connected)
    {
        ControlsPanel.Visibility = connected || optimising ? Visibility.Visible : Visibility.Collapsed;
        OptimiseButton.Visibility = optimising || !canOptimise || !optimiserEnabled ? Visibility.Collapsed : Visibility.Visible;
        OptimiseButton.IsEnabled = !planShowing;
        OptimiserOffNote.Visibility = !optimiserEnabled && !optimising && connected ? Visibility.Visible : Visibility.Collapsed;
        DiagnoseButton.Visibility = !diagnoseAutomatically && connected && !optimising && DiagnosisPanel.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        GoalPanel.Visibility = (canOptimise && optimiserEnabled) || optimising ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = optimising ? Visibility.Visible : Visibility.Collapsed;
        RevertButton.IsEnabled = !optimising;
        GoalSelector.IsEnabled = !optimising && !planShowing;
    }

    private void HidePlan()
    {
        planShowing = false;
        PlanPanel.Visibility = Visibility.Collapsed;
        UpdateControls(gameConnected);
    }

    private string RemainingText(ProgressMessage progress)
    {
        if (progress.StepsDone > 0 && progress.RemainingSecondsHigh > 0)
        {
            return new TimeRange(TimeSpan.FromSeconds(progress.RemainingSecondsLow), TimeSpan.FromSeconds(progress.RemainingSecondsHigh)).Describe() + " left";
        }

        if (progress.StepsDone > 0 && progress.StepsDone >= progress.StepsExpected)
        {
            return "almost done";
        }

        if (plan is { CanStart: true, SecondsHigh: > 0 } planned)
        {
            var low = Math.Max(0, planned.SecondsLow - progress.ElapsedSeconds);
            var high = Math.Max(low, planned.SecondsHigh - progress.ElapsedSeconds);
            return new TimeRange(TimeSpan.FromSeconds(low), TimeSpan.FromSeconds(high)).Describe() + " left, as planned";
        }

        return "working out the time left";
    }

    private static string Clock(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{seconds / SecondsPerMinute}:{seconds % SecondsPerMinute:00}");

    private void DrawChart()
    {
        var width = ChartArea.ActualWidth;
        var height = ChartArea.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var top = Math.Max(SmallestChartTopMs, (chartValues.Count > 0 ? chartValues.Max() : 0) * ChartHeadroom);
        PlaceGuide(SixtyGuide, SixtyFpsFrameMs, top, width, height);
        PlaceGuide(ThirtyGuide, ThirtyFpsFrameMs, top, width, height);
        var points = new PointCollection();
        for (var index = 0; index < chartValues.Count; index++)
        {
            var x = chartValues.Count == 1 ? 0 : index * width / (chartValues.Count - 1);
            var y = height - (Math.Min(chartValues[index], top) / top * height);
            points.Add(new Point(x, y));
        }

        FrameLine.Points = points;
    }

    private static void PlaceGuide(Microsoft.UI.Xaml.Shapes.Line guide, double frameMs, double top, double width, double height)
    {
        var y = height - (frameMs / top * height);
        guide.X1 = 0;
        guide.X2 = width;
        guide.Y1 = y;
        guide.Y2 = y;
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void OnGoalChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        GoalDescription.Text = SelectedGoal switch
        {
            Goal.Balanced => "Most performance for the least visual cost; stops lowering quality once the target is met.",
            Goal.MaximumFps => "Highest frame rate. Accepts bigger visual trade-offs and never caps the frame rate.",
            Goal.Quality => "Keeps visuals high: only low-impact changes, just enough to reach the target.",
            _ => "Favours steady frame times; caps the frame rate when that helps.",
        };

    private void OnOptimiseClicked(object sender, RoutedEventArgs e)
    {
        plannedGoal = SelectedGoal;
        PlanRequested?.Invoke(plannedGoal);
    }

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        HidePlan();
        OptimiseRequested?.Invoke(plannedGoal);
    }

    private void OnCancelPlanClicked(object sender, RoutedEventArgs e)
    {
        plan = null;
        HidePlan();
    }

    private void OnDiagnoseClicked(object sender, RoutedEventArgs e)
    {
        DiagnoseButton.Visibility = Visibility.Collapsed;
        DiagnoseRequested?.Invoke();
    }

    private void OnStopClicked(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    private void OnRevertClicked(object sender, RoutedEventArgs e) => RevertAllRequested?.Invoke();

    private void OnGameSettingsClicked(object sender, RoutedEventArgs e) => GameSettingsRequested?.Invoke();

    private void OnApproveClicked(object sender, RoutedEventArgs e) => Answer(true);

    private void OnSkipClicked(object sender, RoutedEventArgs e) => Answer(false);

    private void Answer(bool approved)
    {
        ProposalPanel.Visibility = Visibility.Collapsed;
        ProposalAnswered?.Invoke(approved);
    }

    private static string Number(double value, string format) => value > 0 ? value.ToString(format, CultureInfo.InvariantCulture) : NoValue;
}
