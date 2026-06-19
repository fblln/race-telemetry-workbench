using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RaceTelemetry.AgentApi.Sessions;
using System.Text.Json;

namespace RaceTelemetry.AgentApi.AgUi;

public static class AgUiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAgUiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/ag-ui", HandleRunAsync)
            .WithName("AgUiRun")
            .DisableAntiforgery();

        app.MapDelete("/api/agent/sessions/{threadId}", HandleDeleteSessionAsync)
            .WithName("DeleteSession");

        return app;
    }

    private static async Task HandleRunAsync(
        HttpContext ctx,
        [FromBody] AgUiRequest request,
        AgentRunner runner,
        AgentSessionRegistry registry,
        ILogger<AgentRunner> logger,
        CancellationToken cancellationToken)
    {
        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        var runId = request.RunId ?? Guid.NewGuid().ToString();

        SessionEntry session;
        try
        {
            session = await registry.GetOrCreateAsync(request.ThreadId, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            await WriteSseEventAsync(ctx, AgUiEvent.RunError(ex.Message, "INVALID_THREAD_ID"), cancellationToken);
            return;
        }
        catch (InvalidOperationException ex)
        {
            await WriteSseEventAsync(ctx, AgUiEvent.RunError(ex.Message, "CAPACITY_EXCEEDED"), cancellationToken);
            return;
        }

        // Serialize turns for the same session; different sessions run concurrently
        var acquired = await session.TurnLock.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        if (!acquired)
        {
            await WriteSseEventAsync(ctx, AgUiEvent.RunError("Session is busy.", "SESSION_BUSY"), cancellationToken);
            return;
        }

        try
        {
            await foreach (var evt in runner.RunAsync(request.ThreadId, runId, request, session, cancellationToken))
            {
                await WriteSseEventAsync(ctx, evt, cancellationToken);
            }
        }
        finally
        {
            session.TurnLock.Release();
        }
    }

    private static Task HandleDeleteSessionAsync(
        string threadId,
        AgentSessionRegistry registry)
    {
        registry.Remove(threadId);
        return Task.CompletedTask;
    }

    private static async Task WriteSseEventAsync(HttpContext ctx, AgUiEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt, JsonOptions);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
