using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class OverviewView : ContentView
{
    private readonly OverviewViewModel _vm;

    public OverviewView(OverviewViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        await _vm.LoadCommand.ExecuteAsync(null);
    }
}
