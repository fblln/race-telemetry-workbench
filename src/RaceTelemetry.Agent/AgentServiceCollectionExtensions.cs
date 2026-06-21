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
            var inner = openAiClient.GetChatClient(opts.Model).AsIChatClient();

            // Log every LLM call's full input/output. UseOpenTelemetry emits a gen_ai span per call
            // (under our already-traced "RaceTelemetry.Agent" source, so it shows in the Aspire
            // dashboard); EnableSensitiveData attaches the prompt messages and completion to that span.
            // UseLogging mirrors the same to ILogger for grepping the console. Both carry prompt text,
            // so keep them to debug builds — gate before shipping if prompts are sensitive.
            return inner.AsBuilder()
                .UseOpenTelemetry(sourceName: AgentTelemetry.SourceName, configure: o => o.EnableSensitiveData = true)
                .UseLogging()
                .Build(sp);
        });

        services.AddSingleton<McpToolRegistry>();

        return services;
    }
}
