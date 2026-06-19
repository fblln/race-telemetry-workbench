using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RaceTelemetry.Agent.Options;

namespace RaceTelemetry.Agent;

public sealed class McpToolRegistry : IAsyncDisposable
{
    private McpClient? _client;
    private IList<McpClientTool>? _tools;
    private readonly TelemetryAgentOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpToolRegistry> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public McpToolRegistry(
        IOptions<TelemetryAgentOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<McpToolRegistry> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsReady => _tools is not null;

    public IReadOnlyList<AITool> GetTools() =>
        (_tools ?? []).Cast<AITool>().ToList();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_tools is not null) return;

            var endpoint = new Uri(_options.McpEndpoint);
            _logger.LogInformation("Connecting to MCP server at {Endpoint}", endpoint);

            var httpClient = _httpClientFactory.CreateClient("mcp");
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                httpClient,
                loggerFactory: null,
                ownsHttpClient: false);

            _client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "race-telemetry-agent",
                        Version = "1.0.0",
                    },
                },
                loggerFactory: null,
                cancellationToken: cancellationToken);

            _tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);

            _logger.LogInformation("MCP connected: discovered {Count} tools", _tools.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MCP server at {Endpoint}", _options.McpEndpoint);
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        _initLock.Dispose();
    }
}
