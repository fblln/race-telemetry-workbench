using System.ComponentModel.DataAnnotations;

namespace RaceTelemetry.Agent.Options;

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    [Required(AllowEmptyStrings = false)]
    public required string ApiKey { get; init; }

    [Required(AllowEmptyStrings = false)]
    public required string Model { get; init; }
}
