using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class LauncherPage : ContentPage
{
    private readonly LauncherViewModel _vm;

    public LauncherPage(LauncherViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_vm.Sessions.Count == 0)
            await _vm.LoadCommand.ExecuteAsync(null);
    }
}
