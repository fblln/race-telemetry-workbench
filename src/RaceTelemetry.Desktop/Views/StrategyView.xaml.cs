using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class StrategyView : ContentView
{
    private readonly StrategyViewModel _vm;
    private readonly StrategyGanttDrawable _drawable = new();
    private bool _loaded;

    public StrategyView(StrategyViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        GanttView.Drawable = _drawable;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await _vm.LoadCommand.ExecuteAsync(null);
            if (_vm.Drivers.Count > 0)
            {
                _drawable.Drivers = _vm.Drivers;
                _drawable.TotalLaps = _vm.TotalLaps;
                GanttView.Invalidate();
            }
        };
    }
}
