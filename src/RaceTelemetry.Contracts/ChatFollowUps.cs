namespace RaceTelemetry.Contracts;

/// <summary>
/// Shared convention for the follow-up question block the agent appends to a
/// finalized answer. The body and the trailing questions are split here so the
/// agent (producer) and the chat UI (consumer) agree on the format.
/// </summary>
public static class ChatFollowUps
{
    public const string Marker = "---FOLLOWUP---";

    /// <summary>
    /// Splits a finalized answer into its visible body and the follow-up
    /// questions trailing the marker. Question lines may be bulleted, numbered,
    /// or quoted. Returns an empty list when no marker is present.
    /// </summary>
    public static (string Body, IReadOnlyList<string> FollowUps) Split(string content)
    {
        var index = content.IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return (content, []);
        }

        var body = content[..index].TrimEnd();
        var followUps = content[(index + Marker.Length)..]
            .Split('\n')
            .Select(line => line.Trim('-', '*', '•', '"', ' ', '\t', '\r'))
            .Where(line => line.Length > 0)
            .ToArray();
        return (body, followUps);
    }
}
