using Gapo.App.Reports;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gapo.App.Views;

public sealed partial class ResultsView : UserControl
{
    public ResultsView(ReportView report, bool canAct)
    {
        Report = report;
        CanAct = canAct;
        InitializeComponent();
    }

    public event EventHandler? MoreOptionsRequested;

    public event EventHandler? RevertAllRequested;

    public event EventHandler? OptimiseAgainRequested;

    public bool CanAct { get; }

    public ReportView Report { get; }

    private void OnMoreOptionsClicked(object sender, RoutedEventArgs e) => MoreOptionsRequested?.Invoke(this, EventArgs.Empty);

    private void OnRevertAllClicked(object sender, RoutedEventArgs e) => RevertAllRequested?.Invoke(this, EventArgs.Empty);

    private void OnOptimiseAgainClicked(object sender, RoutedEventArgs e) => OptimiseAgainRequested?.Invoke(this, EventArgs.Empty);
}
