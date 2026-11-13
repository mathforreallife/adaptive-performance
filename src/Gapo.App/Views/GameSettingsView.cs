using Gapo.Core.Optimisation;
using Gapo.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Gapo.App.Views;

public sealed class GameSettingsView : UserControl
{
    private const double MaximumWidth = 960;
    private const double LabelColumnWidth = 200;
    private const double ValueColumnWidth = 180;
    private const double LockColumnWidth = 190;

    private readonly StackPanel rows = new() { Spacing = 10 };
    private readonly TextBlock title = new();
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap };
    private bool updating;
    private string shown = string.Empty;

    public GameSettingsView()
    {
        title.Style = (Style)Application.Current.Resources["GapoTitleStyle"];
        note.Style = (Style)Application.Current.Resources["GapoSecondaryStyle"];
        var back = new Button { Content = "Back" };
        back.Click += (_, _) => BackRequested?.Invoke();
        var header = new StackPanel { Spacing = 4, Children = { title, note } };
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["GapoCardStyle"],
            Child = new StackPanel { Spacing = 16, Children = { header, rows } },
        };
        Content = new StackPanel { Spacing = 16, MaxWidth = MaximumWidth, Children = { card, back } };
    }

    public event Action? BackRequested;

    public event Action<string, string>? ValueChosen;

    public event Action<string, bool>? LockChanged;

    public event Action<string>? PutBackRequested;

    public void Show(GameSettingsMessage settings)
    {
        var fingerprint = string.Join("|", settings.Settings.Select(setting => $"{setting.Key}={setting.Value}/{setting.OriginalValue}/{setting.Locked}")) + settings.CanChange + settings.Note;
        if (fingerprint == shown)
        {
            return;
        }

        shown = fingerprint;
        updating = true;
        title.Text = settings.GameName + ": settings";
        note.Text = settings.Note;
        rows.Children.Clear();
        rows.Children.Add(HeaderRow());
        foreach (var setting in settings.Settings)
        {
            rows.Children.Add(Row(setting, settings.CanChange));
        }

        updating = false;
    }

    private static Grid HeaderRow()
    {
        var grid = Columns();
        Add(grid, 0, Secondary("Setting"));
        Add(grid, 1, Secondary("Value"));
        Add(grid, 2, Secondary("GAPO may change it"));
        Add(grid, 3, Secondary("Original"));
        return grid;
    }

    private Grid Row(SettingState setting, bool canChange)
    {
        var grid = Columns();
        Add(grid, 0, new TextBlock { Text = setting.Label, Style = (Style)Application.Current.Resources["GapoBodyStyle"], VerticalAlignment = VerticalAlignment.Center });
        if (setting.Choices.Count > 0)
        {
            var picker = new ComboBox { MinWidth = ValueColumnWidth - 12, IsEnabled = canChange };
            AutomationProperties.SetName(picker, setting.Label + " value");
            foreach (var choice in setting.Choices)
            {
                picker.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
            }

            picker.SelectedIndex = IndexOf(setting);
            picker.SelectionChanged += (_, _) =>
            {
                if (!updating && picker.SelectedItem is ComboBoxItem { Tag: string value } && !SettingDefinition.SameValue(value, setting.Value))
                {
                    ValueChosen?.Invoke(setting.Key, value);
                }
            };
            Add(grid, 1, picker);
        }
        else
        {
            Add(grid, 1, new TextBlock { Text = setting.Value, Style = (Style)Application.Current.Resources["GapoBodyStyle"], VerticalAlignment = VerticalAlignment.Center });
        }

        var allow = new ToggleSwitch { IsOn = !setting.Locked, OnContent = "Yes", OffContent = "No, locked", MinWidth = 0 };
        AutomationProperties.SetName(allow, "Let GAPO change " + setting.Label);
        allow.Toggled += (_, _) =>
        {
            if (!updating)
            {
                LockChanged?.Invoke(setting.Key, !allow.IsOn);
            }
        };
        Add(grid, 2, allow);

        var original = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        original.Children.Add(new TextBlock { Text = Describe(setting, setting.OriginalValue), Style = (Style)Application.Current.Resources["GapoSecondaryStyle"], VerticalAlignment = VerticalAlignment.Center });
        if (setting.ChangedFromOriginal && canChange)
        {
            var putBack = new Button { Content = "Put back" };
            AutomationProperties.SetName(putBack, "Put back " + setting.Label);
            putBack.Click += (_, _) => PutBackRequested?.Invoke(setting.Key);
            original.Children.Add(putBack);
        }

        Add(grid, 3, original);
        return grid;
    }

    private static int IndexOf(SettingState setting)
    {
        for (var index = 0; index < setting.Choices.Count; index++)
        {
            if (SettingDefinition.SameValue(setting.Choices[index].Value, setting.Value))
            {
                return index;
            }
        }

        return -1;
    }

    private static string Describe(SettingState setting, string value) =>
        setting.Choices.FirstOrDefault(choice => SettingDefinition.SameValue(choice.Value, value))?.Label ?? value;

    private static Grid Columns()
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LockColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private static TextBlock Secondary(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["GapoSectionLabelStyle"] };

    private static void Add(Grid grid, int column, FrameworkElement element)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
