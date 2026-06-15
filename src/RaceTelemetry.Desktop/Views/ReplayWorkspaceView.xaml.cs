using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class ReplayWorkspaceView : ContentView
{
    private readonly ReplayWorkspaceViewModel _vm;
    private IDispatcherTimer? _timer;
    private bool _isProgrammaticSeek;

    public ReplayWorkspaceView(ReplayWorkspaceViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        MapView.Drawable = vm.MapDrawable;
        WaveView.Drawable = vm.WaveDrawable;

        vm.RenderInvalidated += OnRenderInvalidated;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => _vm.Tick();
        _timer.Start();

        await _vm.LoadCommand.ExecuteAsync(null);
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _timer?.Stop();
    }

    private void OnRenderInvalidated(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _isProgrammaticSeek = true;
            MapView.Invalidate();
            WaveView.Invalidate();
            _isProgrammaticSeek = false;
        });
    }

    private async void OnSeekValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_isProgrammaticSeek) return;
        if (_vm.IsPlaying) return;
        if (Math.Abs(e.NewValue - e.OldValue) < 250) return;
        await _vm.SeekAsync(e.NewValue);
    }
}
