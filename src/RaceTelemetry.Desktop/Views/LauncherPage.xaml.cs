using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class LauncherPage : ContentPage
{
    private readonly LauncherViewModel _vm;
    private readonly CommandPaletteViewModel _palette;

    public LauncherPage(LauncherViewModel vm, CommandPaletteViewModel palette)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        _palette = palette;
        Palette.BindingContext = palette;
        // No quick actions on the launcher — the palette here is pure session search.
        palette.SetQuickActions(System.Array.Empty<PaletteAction>());
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_vm.Circuits.Count == 0)
            await _vm.LoadCommand.ExecuteAsync(null);
    }

    private async void OnOpenPalette(object? sender, EventArgs e) => await _palette.OpenAsync();

    private async void OnOpenPaletteTapped(object? sender, TappedEventArgs e) => await _palette.OpenAsync();

    private void OnSelectNext(object? sender, EventArgs e) => _palette.SelectNextCommand.Execute(null);

    private void OnSelectPrevious(object? sender, EventArgs e) => _palette.SelectPreviousCommand.Execute(null);
}
