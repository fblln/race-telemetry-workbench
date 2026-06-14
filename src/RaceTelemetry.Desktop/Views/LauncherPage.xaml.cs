using System.Collections.Specialized;
using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class LauncherPage : ContentPage
{
    private const double CircuitCardTargetWidth = 430;
    private const double CircuitCardHeight = 132;
    private const double CircuitCardSpacing = 14;
    private const int MaxVisibleCircuitRows = 3;
    private const double DriverChipTargetWidth = 230;
    private const double DriverChipSpacing = 10;
    private const double DriverChipRowHeight = 62;
    private const int MaxVisibleDriverRows = 4;

    private readonly LauncherViewModel _vm;
    private readonly CommandPaletteViewModel _palette;
    private int _circuitGridColumns = 5;
    private int _driverGridColumns = 8;
    private int _lastVisibleCircuitItemIndex = -1;

    public LauncherPage(LauncherViewModel vm, CommandPaletteViewModel palette)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        _palette = palette;
        Palette.BindingContext = palette;
        // No quick actions on the launcher — the palette here is pure session search.
        palette.SetQuickActions(System.Array.Empty<PaletteAction>());
        SizeChanged += OnSizeChanged;
        _vm.Circuits.CollectionChanged += OnCircuitsChanged;
        _vm.Drivers.CollectionChanged += OnDriversChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_vm.Circuits.Count == 0)
            await _vm.LoadCommand.ExecuteAsync(null);
    }

    private async void OnOpenPalette(object? sender, EventArgs e) => await _palette.OpenAsync();

    private void OnClosePalette(object? sender, EventArgs e) => _palette.Close();

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        UpdateCircuitGridMetrics();
        UpdateDriverGridMetrics();
    }

    private void OnCircuitsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _lastVisibleCircuitItemIndex = -1;
        UpdateCircuitGridMetrics();
    }

    private void OnDriversChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateDriverGridMetrics();

    private void OnCircuitCollectionScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        _lastVisibleCircuitItemIndex = e.LastVisibleItemIndex;
        UpdateCircuitOverflowBadge();
    }

    private void UpdateCircuitGridMetrics()
    {
        var width = CircuitCollection.Width;
        if (width > 0)
        {
            var span = Math.Max(1, (int)Math.Floor((width + CircuitCardSpacing) / (CircuitCardTargetWidth + CircuitCardSpacing)));
            if (span != _circuitGridColumns)
            {
                _circuitGridColumns = span;
                CircuitGridLayout.Span = span;
            }
        }

        var visibleRows = Math.Min(MaxVisibleCircuitRows, GetCircuitRowCount());
        var height = visibleRows <= 0
            ? CircuitCardHeight
            : visibleRows * CircuitCardHeight + Math.Max(0, visibleRows - 1) * CircuitCardSpacing + 6;

        CircuitCollection.HeightRequest = height;
        UpdateCircuitOverflowBadge();
    }

    private void UpdateDriverGridMetrics()
    {
        var width = DriverCollectionHost.Width;
        if (width > 0)
        {
            var span = Math.Max(1, (int)Math.Floor((width + DriverChipSpacing) / (DriverChipTargetWidth + DriverChipSpacing)));
            if (span != _driverGridColumns)
            {
                _driverGridColumns = span;
                DriverGridLayout.Span = span;
            }
        }

        var visibleRows = Math.Min(MaxVisibleDriverRows, GetDriverRowCount());
        var height = visibleRows <= 0
            ? DriverChipRowHeight
            : visibleRows * DriverChipRowHeight + Math.Max(0, visibleRows - 1) * DriverChipSpacing;

        DriverCollectionHost.HeightRequest = height;
        DriverCollection.HeightRequest = height;
    }

    private void UpdateCircuitOverflowBadge()
    {
        var hiddenRows = GetHiddenCircuitRows();
        var lastItemIndex = _vm.Circuits.Count - 1;
        var lastVisibleIndex = _lastVisibleCircuitItemIndex >= 0
            ? _lastVisibleCircuitItemIndex
            : EstimateLastVisibleCircuitIndex();
        var hasMoreBelow = hiddenRows > 0 && lastVisibleIndex < lastItemIndex;

        CircuitOverflowBadge.IsVisible = hasMoreBelow;
        CircuitOverflowLabel.Text = hiddenRows == 1
            ? "Scroll for 1 more row"
            : $"Scroll for {hiddenRows} more rows";
    }

    private int GetHiddenCircuitRows()
    {
        if (_vm.Circuits.Count == 0)
            return 0;

        return Math.Max(0, GetCircuitRowCount() - MaxVisibleCircuitRows);
    }

    private int EstimateLastVisibleCircuitIndex()
    {
        var visibleRows = Math.Min(MaxVisibleCircuitRows, GetCircuitRowCount());
        return Math.Min(_vm.Circuits.Count - 1, visibleRows * _circuitGridColumns - 1);
    }

    private int GetCircuitRowCount()
    {
        if (_vm.Circuits.Count == 0)
            return 0;

        return (int)Math.Ceiling(_vm.Circuits.Count / (double)_circuitGridColumns);
    }

    private int GetDriverRowCount()
    {
        if (_vm.Drivers.Count == 0)
            return 0;

        return (int)Math.Ceiling(_vm.Drivers.Count / (double)_driverGridColumns);
    }
}
