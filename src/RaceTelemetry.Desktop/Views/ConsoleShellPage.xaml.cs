using System.ComponentModel;
using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class ConsoleShellPage : ContentPage
{
    private readonly ConsoleShellViewModel _vm;
    private readonly CommandPaletteViewModel _palette;
    private readonly ReportsAiViewModel _reportsAiVm;
    private LauncherView? _launcher;
    private ReportsAiView? _reportsAi;

    public ConsoleShellPage(ConsoleShellViewModel vm, CommandPaletteViewModel palette, ReportsAiViewModel reportsAiVm)
    {
        InitializeComponent();

        _vm = vm;
        _palette = palette;
        _reportsAiVm = reportsAiVm;
        BindingContext = vm;

        _palette.SetOpenConsoleAction(() => _vm.OpenFromPaletteAsync());
        Palette.BindingContext = palette;

        vm.PropertyChanged += OnVmPropertyChanged;
        SwapContent();

        _ = vm.LoadCommand.ExecuteAsync(null);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConsoleShellViewModel.State) or nameof(ConsoleShellViewModel.ActiveView))
            SwapContent();
    }

    private void SwapContent()
    {
        ContentHost.Content = ResolveView();
    }

    private View ResolveView()
    {
        if (_vm.IsPreSession)
        {
            _launcher ??= new LauncherView { BindingContext = _vm };
            return _launcher;
        }

        return _vm.ActiveView?.Index switch
        {
            9 => ResolveReportsAi(),
            _ => new PlaceholderView(),
        };
    }

    private ReportsAiView ResolveReportsAi()
    {
        if (_reportsAi is null)
        {
            _reportsAi = new ReportsAiView();
            _reportsAi.Initialize(_vm._appState.SessionId ?? string.Empty, _reportsAiVm);
        }
        return _reportsAi;
    }

    private void OnOpenPalette(object? sender, EventArgs e)
        => _palette.OpenCommand.Execute(null);

    protected override void OnDisappearing()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        base.OnDisappearing();
    }
}
