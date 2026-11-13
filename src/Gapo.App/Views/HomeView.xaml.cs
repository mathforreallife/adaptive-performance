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

    public HomeView()
    {
        InitializeComponent();
        GoalSelector.SelectedItem = GoalSelector.Items[0];
    }

    public event Action<Goal>? OptimiseRequested;

    public event Action? StopRequested;

    public event Action? RevertAllRequested;

    public Goal SelectedGoal => Goals[Math.Max(0, GoalSelector.Items.IndexOf(GoalSelector.SelectedItem))];

    public void ShowStatus(StatusMessage status)
    {
        optimising = status.State == AgentState.Optimising;
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
        UpdateControls(sample.GameConnected);
        if (!sample.GameConnected)
        {
            GameTitle.Text = "No game is running";
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

    private void UpdateControls(bool gameConnected)
    {
        ControlsPanel.Visibility = gameConnected ? Visibility.Visible : Visibility.Collapsed;
        OptimiseButton.Visibility = optimising ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = optimising ? Visibility.Visible : Visibility.Collapsed;
        RevertButton.IsEnabled = !optimising;
        GoalSelector.IsEnabled = !optimising;
    }

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

    private void OnOptimiseClicked(object sender, RoutedEventArgs e) => OptimiseRequested?.Invoke(SelectedGoal);

    private void OnStopClicked(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    private void OnRevertClicked(object sender, RoutedEventArgs e) => RevertAllRequested?.Invoke();

    private static string Number(double value, string format) => value > 0 ? value.ToString(format, CultureInfo.InvariantCulture) : NoValue;
}
