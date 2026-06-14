namespace RaceTelemetry.Desktop;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        // Carbon Signal is a dark theme. Force dark so default control and Shell
        // templates (which AppThemeBind to system light/dark) resolve to dark and
        // don't leak light-mode chrome onto the graphite surfaces.
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Carbon Signal density target: 15-inch MacBook Pro Retina
        // (2880x1800 physical pixels, 1440x900 logical points at 2x).
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
