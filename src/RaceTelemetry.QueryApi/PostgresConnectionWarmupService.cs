using Npgsql;

namespace RaceTelemetry.QueryApi;

public sealed class PostgresConnectionWarmupService(
    NpgsqlDataSource dataSource,
    ILogger<PostgresConnectionWarmupService> logger) : IHostedService
{
    private const int ConnectionsToWarm = 8;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tasks = Enumerable.Range(0, ConnectionsToWarm)
                .Select(_ => OpenAndCloseConnectionAsync(cancellationToken));

            await Task.WhenAll(tasks);
            logger.LogInformation("Warmed {ConnectionCount} PostgreSQL connection(s).", ConnectionsToWarm);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PostgreSQL connection warmup failed. The first query may pay connection-open latency.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task OpenAndCloseConnectionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    }
}
