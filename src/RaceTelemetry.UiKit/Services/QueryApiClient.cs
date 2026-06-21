using System.Net.Http.Json;
using System.Text.Json;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Desktop.Services;

/// <summary>
/// Typed, read-only HTTP client over the Query API (§5, §6). The desktop never
/// touches the database directly — it consumes Query API contracts only (§8.1).
/// </summary>
public interface IQueryApiClient
{
    Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(int? year = null, string? eventName = null, string? sessionType = null, CancellationToken ct = default);
    Task<IReadOnlyList<DriverSummary>> GetDriversAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<LapSummary>> GetLapsAsync(string sessionId, string driverCode, CancellationToken ct = default);
    Task<ReplayMetadata?> GetReplayMetadataAsync(string sessionId, CancellationToken ct = default);
    Task<StandingsResponse?> GetStandingsAsync(string sessionId, int? atLap = null, string sortBy = "position", CancellationToken ct = default);
    Task<RaceControlResponse?> GetRaceControlAsync(string sessionId, IEnumerable<string>? types = null, int maxResults = 200, CancellationToken ct = default);
    Task<PositionsResponse?> GetPositionsAsync(string sessionId, IEnumerable<string>? drivers = null, CancellationToken ct = default);
    Task<StintAnalysisResponse?> GetStintsAsync(string sessionId, CancellationToken ct = default);
    Task<ReplayChunkResponse?> GetReplayChunkAsync(string sessionId, long fromMs, long durationMs, IEnumerable<string>? drivers = null, IEnumerable<string>? channels = null, int sampleEvery = 1, CancellationToken ct = default);
    Task<RaceStoryResponse?> GetRaceStoryAsync(string sessionId, CancellationToken ct = default);
}

public sealed class QueryApiClient : IQueryApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public QueryApiClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(int? year = null, string? eventName = null, string? sessionType = null, CancellationToken ct = default)
    {
        var query = BuildQuery(("year", year?.ToString()), ("event", eventName), ("sessionType", sessionType));
        var result = await _http.GetFromJsonAsync<ItemsEnvelope<SessionSummary>>($"/api/sessions{query}", JsonOptions, ct);
        return result?.Items ?? Array.Empty<SessionSummary>();
    }

    public async Task<IReadOnlyList<DriverSummary>> GetDriversAsync(string sessionId, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<ItemsEnvelope<DriverSummary>>($"/api/sessions/{Encode(sessionId)}/drivers", JsonOptions, ct);
        return result?.Items ?? Array.Empty<DriverSummary>();
    }

    public async Task<IReadOnlyList<LapSummary>> GetLapsAsync(string sessionId, string driverCode, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<ItemsEnvelope<LapSummary>>(
            $"/api/sessions/{Encode(sessionId)}/drivers/{Encode(driverCode)}/laps", JsonOptions, ct);
        return result?.Items ?? Array.Empty<LapSummary>();
    }

    public Task<ReplayMetadata?> GetReplayMetadataAsync(string sessionId, CancellationToken ct = default)
        => _http.GetFromJsonAsync<ReplayMetadata>($"/api/sessions/{Encode(sessionId)}/replay/metadata", JsonOptions, ct);

    public Task<StandingsResponse?> GetStandingsAsync(string sessionId, int? atLap = null, string sortBy = "position", CancellationToken ct = default)
    {
        var query = BuildQuery(("atLap", atLap?.ToString()), ("sortBy", sortBy));
        return _http.GetFromJsonAsync<StandingsResponse>($"/api/sessions/{Encode(sessionId)}/standings{query}", JsonOptions, ct);
    }

    public Task<RaceControlResponse?> GetRaceControlAsync(string sessionId, IEnumerable<string>? types = null, int maxResults = 200, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("types", types is null ? null : string.Join(',', types)),
            ("maxResults", maxResults.ToString()));
        return _http.GetFromJsonAsync<RaceControlResponse>($"/api/sessions/{Encode(sessionId)}/race-control{query}", JsonOptions, ct);
    }

    public Task<PositionsResponse?> GetPositionsAsync(string sessionId, IEnumerable<string>? drivers = null, CancellationToken ct = default)
    {
        var query = BuildQuery(("drivers", drivers is null ? null : string.Join(',', drivers)));
        return _http.GetFromJsonAsync<PositionsResponse>($"/api/sessions/{Encode(sessionId)}/positions{query}", JsonOptions, ct);
    }

    public async Task<StintAnalysisResponse?> GetStintsAsync(string sessionId, CancellationToken ct = default)
    {
        // /stints/analyze is a POST that defaults its body server-side; send an
        // empty JSON object so model binding produces the default request.
        using var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"/api/sessions/{Encode(sessionId)}/stints/analyze", body, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<StintAnalysisResponse>(JsonOptions, ct);
    }

    public Task<ReplayChunkResponse?> GetReplayChunkAsync(string sessionId, long fromMs, long durationMs, IEnumerable<string>? drivers = null, IEnumerable<string>? channels = null, int sampleEvery = 1, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("fromMs", fromMs.ToString()),
            ("durationMs", durationMs.ToString()),
            ("drivers", drivers is null ? null : string.Join(',', drivers)),
            ("channels", channels is null ? null : string.Join(',', channels)),
            ("sampleEvery", sampleEvery.ToString()));
        return _http.GetFromJsonAsync<ReplayChunkResponse>($"/api/sessions/{Encode(sessionId)}/replay/chunk{query}", JsonOptions, ct);
    }

    public Task<RaceStoryResponse?> GetRaceStoryAsync(string sessionId, CancellationToken ct = default)
        => _http.GetFromJsonAsync<RaceStoryResponse>($"/api/sessions/{Encode(sessionId)}/story", JsonOptions, ct);

    private static string Encode(string value) => Uri.EscapeDataString(value);

    private static string BuildQuery(params (string Key, string? Value)[] parts)
    {
        var pairs = parts
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}")
            .ToArray();
        return pairs.Length == 0 ? string.Empty : "?" + string.Join('&', pairs);
    }

    private sealed record ItemsEnvelope<T>(IReadOnlyList<T> Items);
}
