using RaceTelemetry.Desktop.Views;

namespace RaceTelemetry.Desktop;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("console", typeof(SessionConsolePage));
    }
}
