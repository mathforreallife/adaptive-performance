using System.Diagnostics;
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

    public event EventHandler? SaveRequested;

    public string? SavedPath { get; private set; }

    public void ShowSaved(string path)
    {
        SavedPath = path;
        SavedText.Text = "Saved to " + path;
        ShowSavedButton.Visibility = Visibility.Visible;
        SavedPanel.Visibility = Visibility.Visible;
    }

    public void ShowSaveFailed(string reason)
    {
        SavedPath = null;
        SavedText.Text = "The report could not be saved: " + reason;
        ShowSavedButton.Visibility = Visibility.Collapsed;
        SavedPanel.Visibility = Visibility.Visible;
    }

    public bool CanAct { get; }

    public ReportView Report { get; }

    private void OnMoreOptionsClicked(object sender, RoutedEventArgs e) => MoreOptionsRequested?.Invoke(this, EventArgs.Empty);

    private void OnRevertAllClicked(object sender, RoutedEventArgs e) => RevertAllRequested?.Invoke(this, EventArgs.Empty);

    private void OnOptimiseAgainClicked(object sender, RoutedEventArgs e) => OptimiseAgainRequested?.Invoke(this, EventArgs.Empty);

    private void OnSaveClicked(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);

    private void OnShowSavedClicked(object sender, RoutedEventArgs e)
    {
        if (SavedPath is { } path && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false })?.Dispose();
        }
    }
}
