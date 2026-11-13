using Gapo.App.Overlay;
using Gapo.App.Preferences;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Gapo.App.Views;

public sealed class SettingsView : UserControl
{
    private const double MaximumWidth = 960;

    private static readonly (OverlayCorner Corner, string Label)[] Corners =
    [
        (OverlayCorner.TopLeft, "Top left"),
        (OverlayCorner.TopRight, "Top right"),
        (OverlayCorner.BottomLeft, "Bottom left"),
        (OverlayCorner.BottomRight, "Bottom right"),
    ];

    private static readonly (ElementTheme Theme, string Label)[] Themes =
    [
        (ElementTheme.Default, "Follow Windows"),
        (ElementTheme.Light, "Light"),
        (ElementTheme.Dark, "Dark"),
    ];

    private readonly ToggleSwitch overlay = Switch("Show the FPS overlay over the game");
    private readonly ComboBox corner = Picker("Overlay corner", Corners.Select(entry => entry.Label));
    private readonly ToggleSwitch beforeAfter = Switch("Show before and after in the overlay");
    private readonly TextBlock overlayStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ToggleSwitch notify = Switch("Windows notification when GAPO needs you");
    private readonly ToggleSwitch flash = Switch("Flash GAPO in the taskbar when it needs you");
    private readonly ToggleSwitch startAgent = Switch("Start GAPO's measuring agent automatically");
    private readonly ComboBox theme = Picker("Theme", Themes.Select(entry => entry.Label));
    private readonly ToggleSwitch ask = Switch("Ask me before each change");
    private readonly ToggleSwitch allowCap = Switch("GAPO may cap the frame rate");
    private readonly ComboBox target = Picker("Frame-rate target", Targets.Select(entry => entry.Label));
    private readonly ComboBox restarts = Picker("Most game restarts per optimisation", RestartChoices.Select(count => count.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static readonly (int Fps, string Label)[] Targets =
    [
        (0, "Match my display"),
        (30, "30 FPS"),
        (60, "60 FPS"),
        (90, "90 FPS"),
        (120, "120 FPS"),
        (144, "144 FPS"),
    ];

    private static readonly int[] RestartChoices = [3, 6, 10];
    private bool updating;

    public SettingsView()
    {
        overlayStatus.Style = (Style)Application.Current.Resources["GapoSecondaryStyle"];
        var back = new Button { Content = "Back" };
        back.Click += (_, _) => BackRequested?.Invoke();
        Content = new StackPanel
        {
            Spacing = 16,
            MaxWidth = MaximumWidth,
            Children =
            {
                Card("FPS overlay", "A small box over the game with its live frames per second and, after an optimisation, the frame rate before and after. It is a separate GAPO window on top of the game, never injected into it.", overlay, corner, beforeAfter, overlayStatus),
                Card("Optimisation", "How far GAPO may go on its own. Locks for single settings are under Game settings on the home page.", ask, allowCap, target, restarts),
                Card("When GAPO needs you", "For example when it asks you to restart the game during an optimisation.", notify, flash),
                Card("Agent", "The agent measures your games in the background. Without it GAPO shows nothing live.", startAgent),
                Card("Appearance", string.Empty, theme),
                back,
            },
        };
        foreach (var control in new Control[] { overlay, beforeAfter, notify, flash, startAgent, ask, allowCap })
        {
            ((ToggleSwitch)control).Toggled += (_, _) => Changed();
        }

        corner.SelectionChanged += (_, _) => Changed();
        theme.SelectionChanged += (_, _) => Changed();
        target.SelectionChanged += (_, _) => Changed();
        restarts.SelectionChanged += (_, _) => Changed();
    }

    public event Action? BackRequested;

    public event Action<AppPreferences>? PreferencesChanged;

    public void Show(AppPreferences preferences, string overlayState)
    {
        updating = true;
        overlay.IsOn = preferences.OverlayEnabled;
        corner.SelectedIndex = Array.FindIndex(Corners, entry => entry.Corner == preferences.OverlayCorner);
        beforeAfter.IsOn = preferences.OverlayShowsBeforeAndAfter;
        notify.IsOn = preferences.NotifyWhenAway;
        flash.IsOn = preferences.FlashTaskbarWhenAway;
        startAgent.IsOn = preferences.StartAgentAutomatically;
        theme.SelectedIndex = Array.FindIndex(Themes, entry => entry.Theme == preferences.Theme);
        ask.IsOn = preferences.AskBeforeEachChange;
        allowCap.IsOn = preferences.AllowFrameCap;
        target.SelectedIndex = Math.Max(0, Array.FindIndex(Targets, entry => entry.Fps == preferences.TargetFps));
        restarts.SelectedIndex = Math.Max(0, Array.IndexOf(RestartChoices, preferences.MostRestarts));
        overlayStatus.Text = overlayState;
        corner.IsEnabled = beforeAfter.IsEnabled = preferences.OverlayEnabled;
        updating = false;
    }

    public void ShowOverlayState(string overlayState) => overlayStatus.Text = overlayState;

    private void Changed()
    {
        if (updating)
        {
            return;
        }

        corner.IsEnabled = beforeAfter.IsEnabled = overlay.IsOn;
        PreferencesChanged?.Invoke(new AppPreferences
        {
            OverlayEnabled = overlay.IsOn,
            OverlayCorner = Corners[Math.Max(0, corner.SelectedIndex)].Corner,
            OverlayShowsBeforeAndAfter = beforeAfter.IsOn,
            NotifyWhenAway = notify.IsOn,
            FlashTaskbarWhenAway = flash.IsOn,
            StartAgentAutomatically = startAgent.IsOn,
            Theme = Themes[Math.Max(0, theme.SelectedIndex)].Theme,
            AskBeforeEachChange = ask.IsOn,
            AllowFrameCap = allowCap.IsOn,
            TargetFps = Targets[Math.Max(0, target.SelectedIndex)].Fps,
            MostRestarts = RestartChoices[Math.Max(0, restarts.SelectedIndex)],
        });
    }

    private static Border Card(string title, string explanation, params FrameworkElement[] controls)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["GapoSectionLabelStyle"] });
        if (explanation.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["GapoSecondaryStyle"] });
        }

        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }

        return new Border { Style = (Style)Application.Current.Resources["GapoCardStyle"], Child = panel };
    }

    private static ToggleSwitch Switch(string header)
    {
        var toggle = new ToggleSwitch { Header = header, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(toggle, header);
        return toggle;
    }

    private static ComboBox Picker(string header, IEnumerable<string> labels)
    {
        var picker = new ComboBox { Header = header, MinWidth = 220 };
        AutomationProperties.SetName(picker, header);
        foreach (var label in labels)
        {
            picker.Items.Add(label);
        }

        return picker;
    }
}
