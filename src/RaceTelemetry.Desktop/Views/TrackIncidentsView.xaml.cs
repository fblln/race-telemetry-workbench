using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class TrackIncidentsView : ContentView
{
    private readonly TrackIncidentsViewModel _vm;
    private readonly TrackMapDrawable _drawable = new();
    private bool _loaded;

    public TrackIncidentsView(TrackIncidentsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        MapView.Drawable = _drawable;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await _vm.LoadCommand.ExecuteAsync(null);
            _drawable.Outline = _vm.Outline;
            _drawable.Incidents = _vm.BuildDots();
            MapView.Invalidate();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Re-highlight the selected hotspot on the map when the list selection changes.
        if (e.PropertyName == nameof(TrackIncidentsViewModel.Selected))
        {
            _drawable.Incidents = _vm.BuildDots();
            MapView.Invalidate();
        }
    }
}
