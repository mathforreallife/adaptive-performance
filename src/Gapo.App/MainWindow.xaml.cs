using Gapo.App.Reports;
using Gapo.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Gapo.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1040;
    private const int DefaultHeight = 720;

    private readonly AppOptions options;
    private ReportView report = ReportView.Empty;

    public MainWindow(AppOptions options)
    {
        this.options = options;
        InitializeComponent();
        Title = "GAPO";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWidth, DefaultHeight));
        Root.RequestedTheme = options.Theme;
        Root.Loaded += OnLoaded;
        LoadReport();
    }

    public void ShowResults()
    {
        var view = new ResultsView(report);
        view.MoreOptionsRequested += (_, _) => ShowDetails();
        Show(view, report.HasResult ? "Optimisation finished" : "No result yet");
    }

    public void ShowDetails(int tab = 0)
    {
        var view = new DetailsView(report, tab);
        view.BackRequested += (_, _) => ShowResults();
        Show(view, "All measurements and reasons");
    }

    private void LoadReport()
    {
        if (options.ReportPath is { } path && File.Exists(path))
        {
            report = ReportView.Load(path);
        }

        OverheadText.Text = report.OverheadText;
        OverheadText.Foreground = (Brush)Application.Current.Resources[report.OverBudget ? "GapoWarnBrush" : "GapoTextSecondaryBrush"];
        if (report.HasResult)
        {
            ShowResults();
        }
        else
        {
            Show(new HomeView(), "No game is being optimised");
        }
    }

    private void Show(UIElement view, string status)
    {
        Body.Content = view;
        StatusText.Text = status;
        BodyScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (options.ScreenshotDirectory is { } directory)
        {
            await new ScreenshotRun(this, Root, directory, report.HasResult).CaptureAllAsync();
            Close();
        }
    }
}
