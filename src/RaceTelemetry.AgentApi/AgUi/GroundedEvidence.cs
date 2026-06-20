using System.Globalization;
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

public sealed record GroundedStreamFrame(string Kind, IReadOnlyList<string> FactIds, string Text);

public sealed partial class GroundedFrameVerifier
{
    private static readonly HashSet<string> AllowedHeadings = new(StringComparer.Ordinal)
    {
        "## Overview",
        "## Strategy",
        "## Performance",
        "## Incidents",
        "## Weather"
    };

    private static readonly string[] CaveatTerms =
        ["available data", "data indicates", "estimate", "estimated", "uncertain", "warning", "degraded", "appears"];

    private static readonly HashSet<string> UppercaseAllowList = new(StringComparer.Ordinal)
    {
        "DRS", "VSC", "SC", "F1", "GPS"
    };

    public bool TryVerify(
        string jsonLine,
        GroundedEvidenceLedger ledger,
        out GroundedStreamFrame? frame,
        out string? error)
    {
        frame = null;
        error = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jsonLine);
        }
        catch (JsonException ex)
        {
            error = $"Malformed frame: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!TryString(root, "k", out var kind) || !TryString(root, "t", out var text) || string.IsNullOrWhiteSpace(text))
            {
                error = "Frame must contain non-empty k and t strings.";
                return false;
            }

            var factIds = root.TryGetProperty("f", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : [];

            if (kind == "heading")
            {
                if (!AllowedHeadings.Contains(text))
                {
                    error = "Heading is not allow-listed.";
                    return false;
                }

                frame = new GroundedStreamFrame(kind, [], text);
                return true;
            }

            if (kind == "followup")
            {
                if (text.Length > 160 || !text.TrimEnd().EndsWith('?'))
                {
                    error = "Follow-up must be a short question.";
                    return false;
                }

                frame = new GroundedStreamFrame(kind, [], text);
                return true;
            }

            if (kind != "claim" || factIds.Length == 0)
            {
                error = "Claims must cite at least one fact.";
                return false;
            }

            var referenced = new List<GroundedEvidenceFact>();
            foreach (var id in factIds)
            {
                if (!ledger.Facts.TryGetValue(id, out var fact))
                {
                    error = $"Unknown fact id: {id}.";
                    return false;
                }

                if (fact.NarrationPolicy == "omit")
                {
                    error = $"Fact {id} is not narratable.";
                    return false;
                }

                referenced.Add(fact);
            }

            var evidenceText = string.Join('\n', referenced.Select(fact => fact.Text + "\n" + fact.Raw));
            if (referenced.Any(fact => fact.QualityStatus == "degraded")
                && !CaveatTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                error = "Degraded evidence requires an explicit caveat.";
                return false;
            }

            foreach (Match match in NumberPattern().Matches(text))
            {
                var token = match.Value.TrimStart('+');
                if (!EvidenceContainsToken(evidenceText, token))
                {
                    error = $"Unsupported numeric token: {match.Value}.";
                    return false;
                }
            }

            foreach (Match match in UppercasePattern().Matches(text))
            {
                var token = match.Value;
                if (!UppercaseAllowList.Contains(token) && !EvidenceContainsToken(evidenceText, token))
                {
                    error = $"Unsupported entity token: {token}.";
                    return false;
                }
            }

            frame = new GroundedStreamFrame(kind, factIds, text);
            return true;
        }
    }

    private static bool EvidenceContainsToken(string evidence, string token)
    {
        if (evidence.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!double.TryParse(token.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var variants = new[]
        {
            number.ToString("0", CultureInfo.InvariantCulture),
            number.ToString("0.0", CultureInfo.InvariantCulture),
            number.ToString("0.00", CultureInfo.InvariantCulture),
            number.ToString("0.000", CultureInfo.InvariantCulture)
        };
        return variants.Any(variant => evidence.Contains(variant, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[+-]?\d+(?:[.,]\d+)?")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\b[A-Z]{2,10}\b")]
    private static partial Regex UppercasePattern();
}
