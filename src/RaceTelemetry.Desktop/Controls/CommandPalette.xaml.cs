using System.ComponentModel;
using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Controls;

public partial class CommandPalette : ContentView
{
    private CommandPaletteViewModel? _vm;

    public CommandPalette()
    {
        InitializeComponent();
        BindingContextChanged += OnBindingContextChanged;
    }

    private void OnBindingContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = BindingContext as CommandPaletteViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Autofocus the query field the moment the palette opens.
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && _vm?.IsOpen == true)
        {
            Dispatcher.Dispatch(() => QueryEntry.Focus());
        }
    }
}
