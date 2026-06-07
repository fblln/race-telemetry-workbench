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
    $"/api/sessions/{session.SessionId}/drivers/LEC/laps/1/telemetry?sampleEvery=1&maxSamples=10",
    cancellation.Token);

Assert(telemetry.Items.Count > 0, "Expected seeded lap telemetry.");

var comparison = await AssertOk<LapComparisonResponse>(
    $"/api/sessions/{session.SessionId}/compare/laps?driverA=LEC&lapA=1&driverB=VER&lapB=1",
    cancellation.Token);

Assert(comparison.Items.Count > 0, "Expected seeded lap comparison points.");

var replayMetadata = await AssertOk<ReplayMetadata>(
    $"/api/sessions/{session.SessionId}/replay/metadata",
    cancellation.Token);

Assert(replayMetadata.AvailableChannels.Contains("speed_kmh"), "Expected speed channel.");
Assert(replayMetadata.ContextChannels.Contains("race_control"), "Expected race-control context.");
Assert(replayMetadata.Drivers.Contains("LEC"), "Expected replay drivers.");

var replayChunk = await AssertOk<ReplayChunkResponse>(
    $"/api/sessions/{session.SessionId}/replay/chunk?fromMs=60000&durationMs=30000&drivers=LEC&sampleEvery=2",
    cancellation.Token);

Assert(replayChunk.Items.Any(item => item.DriverCode == "LEC"), "Expected LEC replay chunk.");

var replayContext = await AssertOk<ReplayContextResponse>(
    $"/api/sessions/{session.SessionId}/replay/context?fromMs=60000&durationMs=300000",
    cancellation.Token);

Assert(replayContext.WeatherSamples.Count > 0, "Expected weather context.");

var eventSearch = await PostOk<TelemetryEventSearchResponse>(
    $"/api/sessions/{session.SessionId}/telemetry-events/search",
    new TelemetryEventSearchRequest(["high_speed"], ["LEC"], 0, 300000, 10),
    cancellation.Token);

Assert(eventSearch.Items.Count > 0, "Expected telemetry-event search results.");

var missing = await http.GetAsync("/api/sessions/not-a-session/drivers", cancellation.Token);
Assert(missing.StatusCode == HttpStatusCode.NotFound, "Unknown sessions should return 404.");

var invalid = await http.GetAsync($"/api/sessions/{session.SessionId}/replay/chunk?fromMs=0&durationMs=1", cancellation.Token);
Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid time ranges should return 400.");

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
