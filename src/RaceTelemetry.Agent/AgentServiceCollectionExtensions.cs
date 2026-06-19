using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;
using RaceTelemetry.Agent.Options;
using System.ClientModel;

namespace RaceTelemetry.Agent;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddRaceTelemetryAgent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OpenAiOptions>()
            .Bind(configuration.GetSection(OpenAiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<TelemetryAgentOptions>()
            .Bind(configuration.GetSection(TelemetryAgentOptions.SectionName));

        services.AddSingleton<IChatClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OpenAiOptions>>().Value;
            var openAiClient = new OpenAIClient(new ApiKeyCredential(opts.ApiKey));
            return openAiClient.GetChatClient(opts.Model).AsIChatClient();
        });

        services.AddSingleton<McpToolRegistry>();

        return services;
    }
}
