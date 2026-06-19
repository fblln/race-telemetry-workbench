using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaceTelemetry.Agent.Options;

namespace RaceTelemetry.AgentApi.Sessions;

internal sealed class SessionCleanupService : BackgroundService
{
    private readonly AgentSessionRegistry _registry;
    private readonly TelemetryAgentOptions _options;
    private readonly ILogger<SessionCleanupService> _logger;

    public SessionCleanupService(
        AgentSessionRegistry registry,
        IOptions<TelemetryAgentOptions> options,
        ILogger<SessionCleanupService> logger)
    {
        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_options.SessionCleanupInterval, stoppingToken);

            var threshold = DateTimeOffset.UtcNow - _options.SessionIdleTimeout;
            var evicted = _registry.RemoveExpired(threshold);
            if (evicted > 0)
                _logger.LogInformation("Evicted {Count} idle sessions; active: {Active}", evicted, _registry.Count);
        }
    }
}
