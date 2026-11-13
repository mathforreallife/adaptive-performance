using Gapo.App.Reports;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gapo.App.Views;

public sealed partial class ResultsView : UserControl
{
    public ResultsView(ReportView report)
    {
        Report = report;
        InitializeComponent();
    }

    public event EventHandler? MoreOptionsRequested;

    public ReportView Report { get; }

    private void OnMoreOptionsClicked(object sender, RoutedEventArgs e) => MoreOptionsRequested?.Invoke(this, EventArgs.Empty);
}
