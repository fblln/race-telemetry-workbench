using CommunityToolkit.Mvvm.ComponentModel;

namespace RaceTelemetry.Desktop.ViewModels;

public sealed partial class ConsoleView : ObservableObject
{
    private readonly Func<bool> _isLockedFn;

    public ConsoleView(string title, string hotkey, int index, Func<bool> isLockedFn)
    {
        Title = title;
        Hotkey = hotkey;
        Index = index;
        _isLockedFn = isLockedFn;
    }

    public int Index { get; }
    public string Title { get; }
    public string Hotkey { get; }

    [ObservableProperty]
    private bool _isActive;

    public bool IsLocked => _isLockedFn();

    public void RefreshLocked() => OnPropertyChanged(nameof(IsLocked));
}
