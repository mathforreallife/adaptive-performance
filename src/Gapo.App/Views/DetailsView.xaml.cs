using Gapo.App.Reports;
using Gapo.Core.Tips;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gapo.App.Views;

public sealed partial class DetailsView : UserControl
{
    private readonly FrameworkElement[] panels;

    public DetailsView(ReportView report, int selectedTab)
    {
        Report = report;
        InitializeComponent();
        panels = [ChangesPanel, DiagnosisPanel, HardwarePanel, TipsPanel, LivePanel];
        UnreadCard.Visibility = report.CouldNotBeRead.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Tabs.SelectedItem = Tabs.Items[Math.Clamp(selectedTab, 0, panels.Length - 1)];
    }

    public event EventHandler? BackRequested;

    public ReportView Report { get; }

    private void OnBackClicked(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private async void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page } && TipAdvisor.IsKnownSettingsPage(page))
        {
            await global::Windows.System.Launcher.LaunchUriAsync(new Uri(page));
        }
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.Items.IndexOf(sender.SelectedItem);
        for (var index = 0; index < panels.Length; index++)
        {
            panels[index].Visibility = index == selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
