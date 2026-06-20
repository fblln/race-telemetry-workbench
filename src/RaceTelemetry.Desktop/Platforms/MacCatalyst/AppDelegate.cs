using Foundation;

namespace RaceTelemetry.Desktop;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    // The UI (and its ⌘K palette / Escape handling) now lives in the BlazorWebView.
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
