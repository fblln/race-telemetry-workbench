namespace RaceTelemetry.Desktop;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Desktop-first: open at a workbench-sized window.
        return new Window(new AppShell())
        {
            Title = "Race Telemetry Workbench",
            Width = 1440,
            Height = 900,
            MinimumWidth = 1100,
            MinimumHeight = 720,
        };
    }
}
