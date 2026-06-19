using RaceTelemetry.Contracts;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaceTelemetry.Desktop.Services;

public interface ITelemetryAgentClient
{
    IAsyncEnumerable<AgUiClientEvent> RunAsync(
        string threadId,
        string message,
        TelemetryWorkspaceContext context,
        CancellationToken cancellationToken);

    Task ResetAsync(string threadId, CancellationToken cancellationToken);
}

public sealed class TelemetryAgentClient : ITelemetryAgentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public TelemetryAgentClient(IHttpClientFactory factory)
        => _http = factory.CreateClient("agent-api");

    public async IAsyncEnumerable<AgUiClientEvent> RunAsync(
        string threadId,
        string message,
        TelemetryWorkspaceContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var runId = Guid.CreateVersion7().ToString();
        var body = new
        {
            threadId,
            runId,
            messages = new[]
            {
                new { id = Guid.CreateVersion7().ToString(), role = "user", content = message }
            },
            state = context,
        };

        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage? response = null;
        string? errorMessage = null;

        try
        {
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                errorMessage = $"Agent API error {(int)response.StatusCode}: {err}";
            }
        }
        catch (OperationCanceledException) { yield break; }
        catch (Exception ex) { errorMessage = $"Connection error: {ex.Message}"; }

        if (errorMessage is not null)
        {
            yield return new AgUiClientEvent { Type = "RUN_ERROR", Message = errorMessage };
            response?.Dispose();
            yield break;
        }

        await using var stream = await response!.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line[6..];
            AgUiClientEvent? evt = null;
            try
            {
                evt = JsonSerializer.Deserialize<AgUiClientEvent>(data, JsonOptions);
            }
            catch { /* skip malformed SSE line */ }

            if (evt is not null)
                yield return evt;
        }

        response?.Dispose();
    }

    public async Task ResetAsync(string threadId, CancellationToken cancellationToken)
    {
        try
        {
            await _http.DeleteAsync($"/api/agent/sessions/{threadId}", cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch { /* best effort delete */ }
    }
}

public sealed class AgUiClientEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("messageId")]
    public string? MessageId { get; init; }

    [JsonPropertyName("delta")]
    public string? Delta { get; init; }

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("toolCallName")]
    public string? ToolCallName { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }
}
