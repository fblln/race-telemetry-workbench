using Foundation;
using Microsoft.Extensions.DependencyInjection;
using RaceTelemetry.Desktop.ViewModels;
using UIKit;

namespace RaceTelemetry.Desktop;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// Close the command palette on Escape. The focused search field swallows the
    /// MAUI menu accelerator, so we catch the key at the end of the responder chain
    /// instead (§2a — the palette advertises an ESC dismiss).
    /// </summary>
    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        foreach (var press in presses)
        {
            if (press is UIPress { Key.KeyCode: UIKeyboardHidUsage.KeyboardK } &&
                press.Key.ModifierFlags.HasFlag(UIKeyModifierFlags.Command))
            {
                var palette = IPlatformApplication.Current?.Services.GetService<CommandPaletteViewModel>();
                if (palette is not null)
                {
                    _ = palette.OpenCommand.ExecuteAsync(null);
                    return;
                }
            }

            if (press is UIPress { Key.KeyCode: UIKeyboardHidUsage.KeyboardEscape })
            {
                var palette = IPlatformApplication.Current?.Services.GetService<CommandPaletteViewModel>();
                if (palette is { IsOpen: true })
                {
                    palette.Close();
                    return;
                }
            }
        }

        base.PressesBegan(presses, evt);
    }
}
