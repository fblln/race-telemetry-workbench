using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.ViewModels;

namespace RaceTelemetry.Desktop.Services;

/// <summary>
/// Streams narrative responses from the Anthropic Messages API using direct HttpClient
/// (no third-party SDK). API key is read from MAUI Preferences at call time so the user
/// can set it in the settings UI without restarting.
/// </summary>
public sealed class AiStorytellerService
{
    private const string ApiEndpoint = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-sonnet-4-6";
    private const string ApiVersion = "2023-06-01";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public AiStorytellerService(IHttpClientFactory factory)
        => _http = factory.CreateClient("anthropic");

    public async IAsyncEnumerable<string> AskAsync(
        RaceStoryResponse context,
        IReadOnlyList<AiChatMessage> history,
        string question,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var apiKey = Preferences.Get("anthropic_api_key", "");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return "No Anthropic API key configured. Set one via Preferences.Set(\"anthropic_api_key\", \"sk-ant-...\").";
            yield break;
        }

        var systemPrompt = BuildSystemPrompt(context);
        var messages = BuildMessages(history, question);

        var request = new
        {
            model = Model,
            max_tokens = 2048,
            system = systemPrompt,
            stream = true,
            messages,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", ApiVersion);
        req.Content = new StringContent(JsonSerializer.Serialize(request, JsonOpts), Encoding.UTF8, "application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // Cannot yield inside catch blocks — capture errors as a string and yield after.
        string? earlyError = null;
        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                earlyError = $"API error {(int)response.StatusCode}: {err}";
            }
        }
        catch (OperationCanceledException) { yield break; }
        catch (Exception ex) { earlyError = $"Network error: {ex.Message}"; }

        if (earlyError is not null)
        {
            yield return earlyError;
            response?.Dispose();
            yield break;
        }

        await using var stream = await response!.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            SseChunk? chunk = null;
            try { chunk = JsonSerializer.Deserialize<SseChunk>(data, JsonOpts); }
            catch { /* malformed SSE line — skip */ }

            if (chunk?.Type == "content_block_delta" && chunk.Delta?.Type == "text_delta")
                yield return chunk.Delta.Text ?? string.Empty;
        }

        response?.Dispose();
    }

    private static string BuildSystemPrompt(RaceStoryResponse ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a Formula 1 race analyst. You have detailed telemetry and timing data for the following session. Answer questions about race strategy, driver performance, incidents, and lap times. Be concise and precise.");
        sb.AppendLine();
        sb.AppendLine($"Session: {ctx.Session.EventName} {ctx.Session.Year} — {ctx.Session.SessionType}");
        if (ctx.Weather is not null)
        {
            var rainfall = ctx.Weather.RainfallObserved ? "rain observed" : "dry";
            sb.AppendLine($"Weather: air {ctx.Weather.AirTempMinC:0}–{ctx.Weather.AirTempMaxC:0}°C, track {ctx.Weather.TrackTempMinC:0}–{ctx.Weather.TrackTempMaxC:0}°C, {rainfall}");
        }
        sb.AppendLine();
        if (ctx.Insights.Count > 0)
        {
            sb.AppendLine("Key insights:");
            foreach (var i in ctx.Insights)
                sb.AppendLine($"- [{i.Kind}] {i.Text}" + (i.Value.HasValue ? $" ({i.Value:0.##}{i.Unit})" : ""));
        }
        if (ctx.Stints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Stint summary:");
            foreach (var s in ctx.Stints)
                sb.AppendLine($"  {s.DriverCode}: stint {s.StintNumber}, {s.Compound ?? "?"}, laps {s.FirstLapNumber}–{s.LastLapNumber}");
        }
        if (ctx.PitStops.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Pit stops:");
            foreach (var p in ctx.PitStops)
                sb.AppendLine($"  Lap {p.LapNumber} {p.DriverCode} → {p.Compound ?? p.Kind}");
        }
        if (ctx.TrackStatusPeriods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Track status periods:");
            foreach (var t in ctx.TrackStatusPeriods)
                sb.AppendLine($"  {t.StatusName}" + (t.Message is not null ? $": {t.Message}" : ""));
        }
        return sb.ToString();
    }

    private static object[] BuildMessages(IReadOnlyList<AiChatMessage> history, string question)
    {
        var msgs = new List<object>(history.Count + 1);
        foreach (var m in history)
            msgs.Add(new { role = m.Role == AiRole.User ? "user" : "assistant", content = m.Content });
        msgs.Add(new { role = "user", content = question });
        return msgs.ToArray();
    }

    private sealed class SseChunk
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("delta")] public SseDelta? Delta { get; set; }
    }

    private sealed class SseDelta
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
