using Microsoft.UI.Xaml;
namespace Music.Desktop;
public partial class App : Application
{
    public MainWindow? Window { get; private set; }
    public App()
    {
        InitializeComponent();
        RequestedTheme = ApplicationTheme.Light;
        UnhandledException += (_, args) => DiagnosticLog.Write("xaml_failure", args.Exception);
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
    }
    public void ActivateWindow() => Window?.Activate();
}
