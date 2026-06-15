using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class LapComparisonView : ContentView
{
    private readonly LapComparisonViewModel _vm;
    private readonly PositionTraceDrawable _drawable = new();
    private bool _loaded;

    public LapComparisonView(LapComparisonViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        TraceView.Drawable = _drawable;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await _vm.LoadCommand.ExecuteAsync(null);
            if (_vm.Lines.Count > 0)
            {
                _drawable.Drivers = _vm.Lines;
                _drawable.FieldSize = _vm.FieldSize;
                TraceView.Invalidate();
            }
        };
    }
}
