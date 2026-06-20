using System.Text.Json;
using System.Text.RegularExpressions;

namespace RaceTelemetry.AgentApi.AgUi;

public sealed record GroundedEvidenceFact(
    string Id,
    string Text,
    string QualityStatus,
    string NarrationPolicy,
    string Raw);

public sealed class GroundedEvidenceLedger
{
    private readonly Dictionary<string, GroundedEvidenceFact> _facts = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, GroundedEvidenceFact> Facts => _facts;

    public void AddToolResult(string toolName, int callIndex, string result)
    {
        var syntheticId = $"tool-{callIndex + 1}-{NormalizeId(toolName)}";
        _facts[syntheticId] = new GroundedEvidenceFact(syntheticId, result, "supported", "assert", result);

        try
        {
            using var document = JsonDocument.Parse(result);
            AddNarrativeFacts(document.RootElement);
        }
        catch (JsonException)
        {
            // Text-only tool results remain available through the synthetic fact.
        }
    }

    public string BuildPrompt(int maximumCharacters)
    {
        var payload = _facts.Values.Select(fact => new
        {
            id = fact.Id,
            text = Truncate(fact.Text, 8_000),
            qualityStatus = fact.QualityStatus,
            narrationPolicy = fact.NarrationPolicy
        });
        return Truncate(JsonSerializer.Serialize(payload), maximumCharacters);
    }

    private void AddNarrativeFacts(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("facts", out var facts) && facts.ValueKind == JsonValueKind.Array)
            {
                foreach (var fact in facts.EnumerateArray())
                {
                    if (fact.ValueKind != JsonValueKind.Object
                        || !TryGetString(fact, "id", out var id)
                        || !TryGetString(fact, "text", out var text))
                    {
                        continue;
                    }

                    var quality = TryGetString(fact, "qualityStatus", out var qualityStatus) ? qualityStatus : "supported";
                    var policy = TryGetString(fact, "narrationPolicy", out var narrationPolicy) ? narrationPolicy : "assert";
                    _facts[id] = new GroundedEvidenceFact(id, text, quality, policy, fact.GetRawText());
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                AddNarrativeFacts(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AddNarrativeFacts(item);
            }
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static string NormalizeId(string value) =>
        Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];
}
