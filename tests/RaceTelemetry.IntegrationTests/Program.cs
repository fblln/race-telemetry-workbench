using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RaceTelemetry.Contracts;
using RaceTelemetry.QueryApi;

Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await using var app = RaceTelemetryApi.CreateApp(["--urls", "http://127.0.0.1:0"]);

await app.StartAsync(cancellation.Token);

var serverAddresses = app.Services
    .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
    .Features
    .Get<IServerAddressesFeature>();

var baseAddress = serverAddresses?.Addresses.Single()
    ?? throw new InvalidOperationException("The API did not publish a test address.");

using var http = new HttpClient { BaseAddress = new Uri(baseAddress) };

var apiInfo = await AssertOk<ApiInfo>("/api/", cancellation.Token);
Assert(apiInfo.Capabilities.Contains("replay-chunks"), "Expected replay chunk capability.");

var sessionsResponse = await AssertOk<SessionsResponse>("/api/sessions", cancellation.Token);
Assert(sessionsResponse.Items.Count == 1, "Expected one seeded session.");

var session = sessionsResponse.Items[0];
Assert(session.SessionId == "2025-italian-grand-prix-r", "Unexpected seeded session id.");

var drivers = await AssertOk<DriversResponse>(
    $"/api/sessions/{session.SessionId}/drivers",
    cancellation.Token);

Assert(drivers.Items.Any(driver => driver.DriverCode == "LEC"), "Expected LEC in seeded drivers.");

var laps = await AssertOk<LapsResponse>(
    $"/api/sessions/{session.SessionId}/drivers/LEC/laps",
    cancellation.Token);

Assert(laps.Items.Count >= 2, "Expected seeded LEC laps.");

var telemetry = await AssertOk<LapTelemetryResponse>(
    $"/api/sessions/{session.SessionId}/drivers/LEC/laps/1/telemetry?channels=speed_kmh&sampleEvery=1&maxSamples=10",
    cancellation.Token);

Assert(telemetry.Items.Count > 0, "Expected seeded lap telemetry.");
Assert(telemetry.Items.All(sample => sample.ThrottlePct is null && sample.BrakePct is null), "Unrequested lap telemetry channels should be null.");

var lapQuality = await AssertOk<LapQualityResponse>(
    $"/api/sessions/{session.SessionId}/drivers/LEC/laps/1/quality",
    cancellation.Token);

Assert(lapQuality.QualityStatus.Length > 0, "Expected lap quality status.");

var comparison = await AssertOk<LapComparisonResponse>(
    $"/api/sessions/{session.SessionId}/compare/laps?driverA=LEC&lapA=1&driverB=VER&lapB=1",
    cancellation.Token);

Assert(comparison.Items.Count > 0, "Expected seeded lap comparison points.");

var distanceComparison = await AssertOk<LapComparisonByDistanceResponse>(
    $"/api/sessions/{session.SessionId}/compare/laps/by-distance?driverA=LEC&lapA=1&driverB=VER&lapB=1",
    cancellation.Token);

Assert(distanceComparison.Items.Count > 0, "Expected seeded distance-aligned lap comparison points.");
Assert(distanceComparison.DeltaSignConvention.Length > 0, "Expected distance-comparison sign convention.");

var replayMetadata = await AssertOk<ReplayMetadata>(
    $"/api/sessions/{session.SessionId}/replay/metadata",
    cancellation.Token);

Assert(replayMetadata.AvailableChannels.Contains("speed_kmh"), "Expected speed channel.");
Assert(replayMetadata.ContextChannels.Contains("race_control"), "Expected race-control context.");
Assert(replayMetadata.Drivers.Contains("LEC"), "Expected replay drivers.");

var replayChunk = await AssertOk<ReplayChunkResponse>(
    $"/api/sessions/{session.SessionId}/replay/chunk?fromMs=60000&durationMs=30000&drivers=LEC&channels=speed_kmh&sampleEvery=2",
    cancellation.Token);

Assert(replayChunk.Items.Any(item => item.DriverCode == "LEC"), "Expected LEC replay chunk.");
Assert(
    replayChunk.Items.SelectMany(item => item.Samples).All(sample =>
        sample.ThrottlePct is null
        && sample.BrakePct is null
        && sample.Gear is null
        && sample.Rpm is null
        && sample.Drs is null
        && sample.X is null
        && sample.Y is null
        && sample.Z is null),
    "Unrequested replay channels should be null.");
Assert(replayChunk.Items.SelectMany(item => item.Samples).All(sample => sample.CarSourceTimeUtc is null || sample.CarSourceTimeUtc <= sample.DateUtc), "Replay provenance should expose source timestamps when available.");

var replayContext = await AssertOk<ReplayContextResponse>(
    $"/api/sessions/{session.SessionId}/replay/context?fromMs=60000&durationMs=300000",
    cancellation.Token);

Assert(replayContext.WeatherSamples.Count > 0, "Expected weather context.");

var eventSearch = await PostOk<TelemetryEventSearchResponse>(
    $"/api/sessions/{session.SessionId}/telemetry/events/search",
    new TelemetryEventSearchRequest(["high_speed"], ["LEC"], 0, 300000, 10),
    cancellation.Token);

Assert(eventSearch.Items.Count > 0, "Expected telemetry-event search results.");

var aggregate = await PostOk<TelemetryAggregateResponse>(
    $"/api/sessions/{session.SessionId}/telemetry/aggregate",
    new TelemetryAggregateRequest(["LEC"], ["driver"], ["sample_count", "avg_speed_kmh"], null, null, 20),
    cancellation.Token);

Assert(aggregate.Items.Count > 0, "Expected telemetry aggregate results.");

var windows = await PostOk<TelemetryWindowResponse>(
    $"/api/sessions/{session.SessionId}/telemetry/windows",
    new TelemetryWindowRequest(["LEC"], "high_speed", null, 0, false, 20),
    cancellation.Token);

Assert(windows.Items.Count > 0, "Expected telemetry window results.");

var stints = await PostOk<StintAnalysisResponse>(
    $"/api/sessions/{session.SessionId}/stints/analyze",
    new StintAnalysisRequest(["LEC"], null, true, 1, null),
    cancellation.Token);

Assert(stints.Items.Count > 0, "Expected stint analysis results.");

var pitStops = await PostOk<PitStopAnalysisResponse>(
    $"/api/sessions/{session.SessionId}/pit-stops/analyze",
    new PitStopAnalysisRequest(["LEC"], 3, 20),
    cancellation.Token);

Assert(pitStops.Items is not null, "Expected pit-stop analysis response.");

var weatherTrend = await PostOk<WeatherTrendResponse>(
    $"/api/sessions/{session.SessionId}/weather/trend",
    new WeatherTrendRequest(0, 300000),
    cancellation.Token);

Assert(weatherTrend.SampleCount >= 0, "Expected weather trend response.");

var raceControl = await PostOk<RaceControlTimelineResponse>(
    $"/api/sessions/{session.SessionId}/race-control/timeline",
    new RaceControlTimelineRequest(null, null, null, null, null, null, null, 20),
    cancellation.Token);

Assert(raceControl.Items is not null, "Expected race-control timeline response.");

var circuitContext = await AssertOk<CircuitContextResponse>(
    $"/api/sessions/{session.SessionId}/circuit/context",
    cancellation.Token);

Assert(circuitContext.Corners is not null, "Expected circuit context response.");

var standings = await AssertOk<StandingsResponse>(
    $"/api/sessions/{session.SessionId}/standings",
    cancellation.Token);

Assert(standings.Items.Count > 0, "Expected standings rows.");
Assert(standings.Items[0].Position == 1, "Standings should start at position 1.");
Assert(
    standings.Items.Zip(standings.Items.Skip(1)).All(pair => pair.First.Position < pair.Second.Position),
    "Standings positions should be strictly increasing.");
Assert(standings.Items.Any(row => row.DriverCode == "LEC"), "Expected LEC in standings.");

var positions = await AssertOk<PositionsResponse>(
    $"/api/sessions/{session.SessionId}/positions?drivers=LEC&fromLap=1&toLap=5",
    cancellation.Token);

Assert(positions.Items.Any(item => item.DriverCode == "LEC"), "Expected LEC positions.");
Assert(
    positions.Items.Single(item => item.DriverCode == "LEC").Positions.Count == 5,
    "Position arrays should align to the requested lap range.");

var incidents = await AssertOk<RaceControlResponse>(
    $"/api/sessions/{session.SessionId}/race-control?maxResults=50",
    cancellation.Token);

Assert(incidents.Summary is not null, "Expected an incident summary.");
Assert(
    incidents.Items.All(item => item.Type is "safety_car" or "vsc" or "yellow" or "red" or "hard_braking" or "off_track" or "spin"),
    "RaceControlItem types should be from the documented set.");

var invalidSort = await http.GetAsync($"/api/sessions/{session.SessionId}/standings?sortBy=bogus", cancellation.Token);
Assert(invalidSort.StatusCode == HttpStatusCode.BadRequest, "Unknown standings sort keys should return 400.");

var missing = await http.GetAsync("/api/sessions/not-a-session/drivers", cancellation.Token);
Assert(missing.StatusCode == HttpStatusCode.NotFound, "Unknown sessions should return 404.");
Assert(missing.Content.Headers.ContentType?.MediaType == "application/problem+json", "404 responses should use problem+json.");
var missingProblem = await missing.Content.ReadFromJsonAsync<ApiProblem>(cancellation.Token);
Assert(missingProblem?.Code == "SessionNotFound", "404 problem should expose a stable code.");

var invalid = await http.GetAsync($"/api/sessions/{session.SessionId}/replay/chunk?fromMs=0&durationMs=1", cancellation.Token);
Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid time ranges should return 400.");
Assert(invalid.Content.Headers.ContentType?.MediaType == "application/problem+json", "400 responses should use problem+json.");
var invalidProblem = await invalid.Content.ReadFromJsonAsync<ApiProblem>(cancellation.Token);
Assert(invalidProblem?.Code == "InvalidTimeRange", "400 problem should expose a stable code.");
Assert(invalidProblem?.Errors?.ContainsKey("durationMs") == true, "400 problem should include invalid field context.");

await app.StopAsync(cancellation.Token);

Console.WriteLine("RaceTelemetry.QueryApi integration checks passed.");

async Task<T> AssertOk<T>(string path, CancellationToken cancellationToken)
{
    using var response = await http.GetAsync(path, cancellationToken);
    Assert(response.StatusCode == HttpStatusCode.OK, $"{path} returned {response.StatusCode}.");

    var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    return value ?? throw new InvalidOperationException($"{path} returned an empty JSON payload.");
}

async Task<T> PostOk<T>(string path, object body, CancellationToken cancellationToken)
{
    using var response = await http.PostAsJsonAsync(path, body, cancellationToken);
    Assert(response.StatusCode == HttpStatusCode.OK, $"{path} returned {response.StatusCode}.");

    var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    return value ?? throw new InvalidOperationException($"{path} returned an empty JSON payload.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
