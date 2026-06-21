using System.Text;
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
        result = UnwrapMcpEnvelope(result);
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
        // Plain-text, not JSON: a JSON evidence blob invites small models to echo it back as the
        // "answer". Numbered text lines read as source material to summarise, not output to copy.
        var builder = new StringBuilder();
        var index = 1;
        foreach (var fact in _facts.Values)
        {
            var quality = fact.QualityStatus is "supported" or "" ? "" : $" ({fact.QualityStatus})";
            builder.Append("Evidence ").Append(index++).Append(quality).Append(": ")
                .AppendLine(Truncate(fact.Text, 16_000));
        }
        return Truncate(builder.ToString(), maximumCharacters);
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

    // MCP tool results arrive wrapped as {"content":[{"type":"text","text":"<json>"}]}. Store the inner
    // payload so the evidence packet is clean data — easier to ground on and less likely to be echoed verbatim.
    private static string UnwrapMcpEnvelope(string result)
    {
        if (string.IsNullOrEmpty(result) || !result.Contains("\"text\""))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            // {"content":[{"type":"text","text":"..."}]}
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var texts = content.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && item.TryGetProperty("text", out _))
                    .Select(item => item.GetProperty("text").GetString() ?? string.Empty)
                    .ToArray();
                var joined = string.Concat(texts);
                if (joined.Length > 0) return joined;
            }

            // {"text":"..."} (single text-content tool result)
            if (root.TryGetProperty("text", out var single) && single.ValueKind == JsonValueKind.String)
            {
                var inner = single.GetString();
                if (!string.IsNullOrEmpty(inner)) return inner;
            }

            return result;
        }
        catch (JsonException)
        {
            return result;
        }
    }

    private static string NormalizeId(string value) =>
        Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];
}
