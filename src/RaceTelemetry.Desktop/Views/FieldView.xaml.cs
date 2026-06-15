using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class FieldView : ContentView
{
    private readonly FieldViewViewModel _vm;
    private bool _loaded;

    public FieldView(FieldViewViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await _vm.LoadCommand.ExecuteAsync(null);
        };
    }
}
