using CommunityToolkit.Mvvm.ComponentModel;

namespace RaceTelemetry.Desktop.ViewModels;

public sealed partial class HudMetric : ObservableObject
{
    public HudMetric(string label, string value, bool isPlaceholder = false)
    {
        _label = label;
        _value = value;
        _isPlaceholder = isPlaceholder;
    }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private bool _isPlaceholder;
}
