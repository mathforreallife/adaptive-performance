using Microsoft.UI.Xaml;

namespace Gapo.App;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow(AppOptions.Parse(Environment.GetCommandLineArgs().Skip(1)));
        window.Activate();
    }
}
