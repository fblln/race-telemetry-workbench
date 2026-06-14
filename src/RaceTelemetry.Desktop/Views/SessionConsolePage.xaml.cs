using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Views;

public partial class SessionConsolePage : ContentPage
{
    private readonly SessionConsoleViewModel _vm;
    private readonly IServiceProvider _services;
    private readonly CommandPaletteViewModel _palette;
    private readonly Dictionary<string, View> _viewCache = new();

    public SessionConsolePage(SessionConsoleViewModel vm, IServiceProvider services)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        _services = services;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        _palette = services.GetRequiredService<CommandPaletteViewModel>();
        Palette.BindingContext = _palette;
        RegisterPaletteActions();
    }

    /// <summary>Quick actions exposed in the ⌘K palette while a session is open (§2a).</summary>
    private void RegisterPaletteActions()
    {
        PaletteAction View(string title, string index, string hotkey) =>
            new(title, $"View · key {hotkey}", "▦", () =>
            {
                _vm.SelectIndexCommand.Execute(index);
                return Task.CompletedTask;
            });

        _palette.SetQuickActions(new[]
        {
            View("Overview", "1", "1"),
            View("Replay workspace", "2", "2"),
            View("Strategy", "3", "3"),
            View("Lap analysis", "4", "4"),
            View("Field view", "5", "5"),
            View("Incidents", "6", "6"),
            new PaletteAction("Back to launcher", "Navigate · ⌘[", "‹", () => _vm.BackToLauncherCommand.ExecuteAsync(null)),
        });
    }

    private async void OnOpenPalette(object? sender, EventArgs e) => await _palette.OpenAsync();

    private void OnClosePalette(object? sender, EventArgs e) => _palette.Close();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadCommand.ExecuteAsync(null);
        ShowActiveView();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionConsoleViewModel.ActiveView))
            ShowActiveView();
    }

    private void ShowActiveView()
    {
        var title = _vm.ActiveView?.Title ?? "Field";
        if (!_viewCache.TryGetValue(title, out var view))
        {
            view = CreateView(title);
            _viewCache[title] = view;
        }

        ContentHost.Content = view;
    }

    private void OnFocusSearch(object? sender, EventArgs e) => SearchEntry.Focus();

    private View CreateView(string title) => title switch
    {
        "Overview" => _services.GetRequiredService<OverviewView>(),
        "Field" => _services.GetRequiredService<FieldView>(),
        "Incidents" => _services.GetRequiredService<TrackIncidentsView>(),
        "Replay" => _services.GetRequiredService<ReplayWorkspaceView>(),
        "Lap analysis" => _services.GetRequiredService<LapComparisonView>(),
        "Strategy" => _services.GetRequiredService<StrategyView>(),
        _ => new PlaceholderView(title),
    };
}
