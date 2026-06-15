using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Replay Workspace (§8.5). Owns the transport state and the current-value
/// readouts. Wire chunk loading (/replay/chunk) and the linked timebase (§7.7)
/// onto the track map and waveform drawables.
/// </summary>
public sealed partial class ReplayWorkspaceViewModel : ObservableObject
{
    private readonly IQueryApiClient _api;
    private readonly ISessionPrefetchService _prefetch;
    private readonly AppState _state;
    private CancellationTokenSource? _loadCts;
    private ReplayMetadata? _metadata;
    private ReplayChunkResponse? _chunk;
    private DateTimeOffset _lastTickUtc;

    private static readonly double[] Speeds = { 0.25, 0.5, 1, 2, 5, 10, 20 };
    private static readonly string[] Channels = { "speed_kmh", "throttle_pct", "brake_pct", "gear", "rpm", "drs", "x", "y" };

    public ReplayWorkspaceViewModel(IQueryApiClient api, ISessionPrefetchService prefetch, AppState state)
    {
        _api = api;
        _prefetch = prefetch;
        _state = state;
    }

    public TrackMapDrawable MapDrawable { get; } = new();

    public WaveformDrawable WaveDrawable { get; } = new();

    public event EventHandler? RenderInvalidated;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _replaySpeed = 1;

    [ObservableProperty]
    private double _positionMs;

    [ObservableProperty]
    private double _durationMs = 1;

    [ObservableProperty]
    private string _clock = "00:00.000";

    [ObservableProperty]
    private string _status = "Load a session to start replay.";

    // Current-value readouts (§8.5 Current Values panel). Bound as text for the scaffold.
    [ObservableProperty] private string _speed = "--";
    [ObservableProperty] private string _throttle = "--";
    [ObservableProperty] private string _brake = "--";
    [ObservableProperty] private string _gear = "--";
    [ObservableProperty] private string _rpm = "--";
    [ObservableProperty] private string _drs = "--";

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_state.SessionId is null) return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
            Status = "Loading replay metadata...";
            var snap = await _prefetch.GetAsync(_state.SessionId, ct);
            _metadata = snap.ReplayMetadata ?? await _api.GetReplayMetadataAsync(_state.SessionId, ct);
            if (_metadata is null)
            {
                Status = "Replay metadata unavailable. Start the Query API and reopen the session.";
                return;
            }

            MapDrawable.Outline = snap.TrackOutline;
            ReplaySpeed = _metadata.DefaultReplaySpeed > 0 ? _metadata.DefaultReplaySpeed : 1;
            DurationMs = Math.Max(1, _metadata.ReplayEndMs);
            PositionMs = Math.Clamp(_metadata.ReplayStartMs, 0, DurationMs);

            await LoadChunkAroundAsync((long)PositionMs, ct);
            ApplyPosition((long)PositionMs);
            Status = "Ready";
        }
        catch (OperationCanceledException)
        {
            // A new load or seek took over.
        }
        catch (Exception ex)
        {
            Status = $"Replay unavailable: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TogglePlayAsync()
    {
        if (_metadata is null)
            await LoadAsync();

        IsPlaying = !IsPlaying;
        _lastTickUtc = DateTimeOffset.UtcNow;
    }

    [RelayCommand]
    private void CycleSpeed()
    {
        var idx = Array.IndexOf(Speeds, ReplaySpeed);
        ReplaySpeed = Speeds[(idx + 1) % Speeds.Length];
    }

    public async Task SeekAsync(double targetMs)
    {
        if (_state.SessionId is null) return;
        var clamped = Math.Clamp(targetMs, 0, DurationMs);
        PositionMs = clamped;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        try
        {
            if (!ChunkContains((long)clamped))
                await LoadChunkAroundAsync((long)clamped, ct);
            ApplyPosition((long)clamped);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = $"Seek failed: {ex.Message}";
        }
    }

    public void Tick()
    {
        if (!IsPlaying || _metadata is null) return;

        var now = DateTimeOffset.UtcNow;
        var elapsed = _lastTickUtc == default ? TimeSpan.Zero : now - _lastTickUtc;
        _lastTickUtc = now;

        var next = PositionMs + elapsed.TotalMilliseconds * ReplaySpeed;
        if (next >= DurationMs)
        {
            next = DurationMs;
            IsPlaying = false;
        }

        PositionMs = next;
        _ = EnsureChunkAndApplyAsync((long)next);
    }

    private async Task EnsureChunkAndApplyAsync(long positionMs)
    {
        if (_state.SessionId is null) return;
        try
        {
            if (!ChunkContains(positionMs))
                await LoadChunkAroundAsync(positionMs, _loadCts?.Token ?? CancellationToken.None);
            ApplyPosition(positionMs);
        }
        catch
        {
            IsPlaying = false;
        }
    }

    private async Task LoadChunkAroundAsync(long positionMs, CancellationToken ct)
    {
        if (_state.SessionId is null || _metadata is null) return;

        var duration = Math.Clamp(_metadata.RecommendedChunkDurationMs, 15_000, 120_000);
        var from = Math.Max(0, positionMs - duration / 3);
        var drivers = _state.SelectedDrivers.Count > 0 ? _state.SelectedDrivers : _metadata.Drivers.Take(6);
        _chunk = await _api.GetReplayChunkAsync(
            _state.SessionId,
            from,
            duration,
            drivers,
            Channels,
            sampleEvery: 3,
            ct).ConfigureAwait(false);

        MainThread.BeginInvokeOnMainThread(() => UpdateWaveform());
    }

    private bool ChunkContains(long positionMs)
        => _chunk is not null && positionMs >= _chunk.FromMs && positionMs <= _chunk.FromMs + _chunk.DurationMs;

    private void ApplyPosition(long positionMs)
    {
        var chunk = _chunk;
        if (chunk is null) return;

        Clock = TimeSpan.FromMilliseconds(positionMs).ToString(@"hh\:mm\:ss\.fff");
        WaveDrawable.CursorFraction = chunk.DurationMs <= 0
            ? 0
            : Math.Clamp((positionMs - chunk.FromMs) / (double)chunk.DurationMs, 0, 1);

        var markers = new List<TrackMapDrawable.DriverMarker>();
        ReplaySample? first = null;
        foreach (var driver in chunk.Items)
        {
            var sample = Nearest(driver.Samples, positionMs);
            if (sample is null) continue;
            first ??= sample;
            if (sample.X is not null && sample.Y is not null)
            {
                markers.Add(new TrackMapDrawable.DriverMarker(
                    driver.DriverCode,
                    DriverPalette.HexFor(driver.DriverCode),
                    sample.X.Value,
                    sample.Y.Value));
            }
        }

        MapDrawable.Drivers = markers;
        if (first is not null)
        {
            Speed = Format(first.SpeedKmh, "0");
            Throttle = Format(first.ThrottlePct, "0");
            Brake = Format(first.BrakePct, "0");
            Gear = first.Gear?.ToString() ?? "--";
            Rpm = Format(first.Rpm, "0");
            Drs = first.Drs is null ? "--" : first.Drs == 0 ? "OFF" : "ON";
        }

        RenderInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateWaveform()
    {
        var firstDriver = _chunk?.Items.FirstOrDefault();
        if (firstDriver is null || firstDriver.Samples.Count < 2) return;

        WaveDrawable.Channels =
        [
            new("speed", "#22A7FF", Normalize(firstDriver.Samples.Select(s => s.SpeedKmh))),
            new("throttle", "#15D981", Normalize(firstDriver.Samples.Select(s => s.ThrottlePct))),
            new("brake", "#FF7A22", Normalize(firstDriver.Samples.Select(s => s.BrakePct))),
        ];
        RenderInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private static ReplaySample? Nearest(IReadOnlyList<ReplaySample> samples, long positionMs)
    {
        ReplaySample? nearest = null;
        var best = long.MaxValue;
        foreach (var sample in samples)
        {
            if (sample.OffsetMs is null) continue;
            var delta = Math.Abs(sample.OffsetMs.Value - positionMs);
            if (delta >= best) continue;
            nearest = sample;
            best = delta;
        }
        return nearest;
    }

    private static IReadOnlyList<double> Normalize(IEnumerable<double?> values)
    {
        var raw = values.Select(v => v ?? double.NaN).ToArray();
        var valid = raw.Where(v => !double.IsNaN(v)).ToArray();
        if (valid.Length == 0)
            return raw.Select(_ => 0.0).ToArray();
        var min = valid.Min();
        var max = valid.Max();
        var span = Math.Max(1, max - min);
        return raw.Select(v => double.IsNaN(v) ? 0 : Math.Clamp((v - min) / span, 0, 1)).ToArray();
    }

    private static string Format(double? value, string format)
        => value is null ? "--" : value.Value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Lap Analysis / position trace (§8.15). Builds one line per driver from the
/// session's classified positions (/positions, §6.12) in the project-owned
/// categorical palette, and feeds them to the PositionTraceDrawable.
/// </summary>
public sealed partial class LapComparisonViewModel : ObservableObject
{
    private readonly ISessionPrefetchService _prefetch;
    private readonly AppState _state;

    public LapComparisonViewModel(ISessionPrefetchService prefetch, AppState state)
    {
        _prefetch = prefetch;
        _state = state;
    }

    [ObservableProperty]
    private string _caption = "Position trace — classified position per lap. Crossings read as overtakes and pit cycles.";

    /// <summary>Lines for the position-trace drawable; empty until a session loads.</summary>
    public IReadOnlyList<PositionTraceDrawable.DriverLine> Lines { get; private set; } = Array.Empty<PositionTraceDrawable.DriverLine>();

    public int FieldSize { get; private set; } = 20;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_state.SessionId is null) return;
        try
        {
            var snapshot = await _prefetch.GetAsync(_state.SessionId);
            var positions = snapshot.Positions;
            if (positions is null || positions.Items.Count == 0)
            {
                Caption = "Position trace unavailable (start the Query API and reopen the session).";
                return;
            }

            // Field size is the largest classified position seen, so the Y axis spans the field.
            var maxPos = positions.Items
                .SelectMany(item => item.Positions)
                .Where(p => p is not null)
                .Select(p => p!.Value)
                .DefaultIfEmpty(20)
                .Max();
            FieldSize = Math.Max(2, maxPos);

            Lines = positions.Items
                .Select(item => new PositionTraceDrawable.DriverLine(
                    item.DriverCode,
                    DriverPalette.HexFor(item.DriverCode),
                    FillGaps(item.Positions)))
                .Where(line => line.Positions.Count >= 2)
                .ToList();
        }
        catch (Exception ex)
        {
            Caption = $"Position trace unavailable: {ex.Message}";
        }
    }

    /// <summary>
    /// Carry-forward fill so a missing classification holds the last known
    /// position (positions only change at a line crossing); leading gaps adopt
    /// the first known value so the line starts cleanly.
    /// </summary>
    private static IReadOnlyList<int> FillGaps(IReadOnlyList<int?> positions)
    {
        var result = new List<int>(positions.Count);
        int? last = positions.FirstOrDefault(p => p is not null);
        foreach (var p in positions)
        {
            if (p is not null) last = p;
            if (last is not null) result.Add(last.Value);
        }
        return result;
    }
}
