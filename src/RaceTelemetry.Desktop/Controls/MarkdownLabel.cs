using System.Text.RegularExpressions;

namespace RaceTelemetry.Desktop.Controls;

/// <summary>
/// Label that renders a subset of Markdown produced by the agent:
/// ## / ### / #### headings, **bold**, ordered lists (1.), unordered lists (- / *),
/// horizontal rules (---), and blank-line paragraph spacing.
/// Updates FormattedText on every MarkdownText change so streaming works character-by-character.
/// </summary>
public sealed partial class MarkdownLabel : Label
{
    public static readonly BindableProperty MarkdownTextProperty =
        BindableProperty.Create(
            nameof(MarkdownText), typeof(string), typeof(MarkdownLabel), default(string),
            propertyChanged: (b, _, n) => ((MarkdownLabel)b).FormattedText = Render(n as string ?? string.Empty));

    public string? MarkdownText
    {
        get => (string?)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    private static readonly Color TextPri  = Color.FromArgb("#F4EEE6");
    private static readonly Color TextSec  = Color.FromArgb("#BCB1A2");
    private static readonly Color TextMut  = Color.FromArgb("#7A736B");
    private static readonly Color Accent   = Color.FromArgb("#FFA60D");

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldPattern();

    private static FormattedString Render(string markdown)
    {
        var fs = new FormattedString();
        if (string.IsNullOrEmpty(markdown)) return fs;

        var lines = markdown.Split('\n');
        int orderedCounter = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw   = lines[i].TrimEnd();
            var isLast = i == lines.Length - 1;

            // Horizontal rule — skip, just emit a small gap
            if (raw is "---" or "***" or "___" or "- - -")
            {
                fs.Spans.Add(new Span { Text = "\n", FontSize = 4 });
                orderedCounter = 0;
                continue;
            }

            // Headings
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                AddHeading(fs, raw[3..], Accent, 15, "InterSemiBold");
                orderedCounter = 0;
            }
            else if (raw.StartsWith("### ", StringComparison.Ordinal))
            {
                AddHeading(fs, raw[4..], TextSec, 12, "InterSemiBold");
                orderedCounter = 0;
            }
            else if (raw.StartsWith("#### ", StringComparison.Ordinal))
            {
                AddHeading(fs, raw[5..], TextMut, 11, "InterSemiBold");
                orderedCounter = 0;
            }
            // Unordered list
            else if (raw.StartsWith("- ", StringComparison.Ordinal) || raw.StartsWith("* ", StringComparison.Ordinal))
            {
                AddBullet(fs, raw[2..]);
                orderedCounter = 0;
            }
            // Ordered list (1., 2., …)
            else if (System.Text.RegularExpressions.Regex.Match(raw, @"^(\d+)\.\s+(.+)") is { Success: true } m)
            {
                orderedCounter++;
                fs.Spans.Add(new Span { Text = $"  {orderedCounter}. ", TextColor = Accent, FontFamily = "JetBrainsMono", FontSize = 13 });
                AddInline(fs, m.Groups[2].Value, TextPri, 14);
            }
            // Blank line
            else if (string.IsNullOrWhiteSpace(raw))
            {
                fs.Spans.Add(new Span { Text = "\n", FontSize = 5 });
                orderedCounter = 0;
            }
            // Normal paragraph
            else
            {
                AddInline(fs, raw, TextPri, 14);
                orderedCounter = 0;
            }

            if (!isLast && !string.IsNullOrWhiteSpace(raw))
                fs.Spans.Add(new Span { Text = "\n" });
        }

        return fs;
    }

    private static void AddHeading(FormattedString fs, string text, Color color, double size, string font)
        => fs.Spans.Add(new Span { Text = text, TextColor = color, FontSize = size, FontAttributes = FontAttributes.Bold, FontFamily = font });

    private static void AddBullet(FormattedString fs, string text)
    {
        fs.Spans.Add(new Span { Text = "  · ", TextColor = Accent, FontFamily = "JetBrainsMono", FontSize = 13 });
        AddInline(fs, text, TextPri, 14);
    }

    private static void AddInline(FormattedString fs, string text, Color baseColor, double size)
    {
        var pos = 0;
        foreach (System.Text.RegularExpressions.Match m in BoldPattern().Matches(text))
        {
            if (m.Index > pos)
                fs.Spans.Add(new Span { Text = text[pos..m.Index], TextColor = baseColor, FontSize = size, FontFamily = "Inter" });
            fs.Spans.Add(new Span { Text = m.Groups[1].Value, TextColor = TextPri, FontSize = size, FontAttributes = FontAttributes.Bold, FontFamily = "InterSemiBold" });
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            fs.Spans.Add(new Span { Text = text[pos..], TextColor = baseColor, FontSize = size, FontFamily = "Inter" });
    }
}
