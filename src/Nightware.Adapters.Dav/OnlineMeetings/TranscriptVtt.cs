using System.Globalization;
using System.Text;

namespace Nightware.Adapters.Dav.OnlineMeetings;

/// <summary>
/// Talk's transcript (Markdown text, then a blank line and its localised AI warning) as WebVTT. Talk keeps no cue times or
/// speakers, so each paragraph becomes one cue spanning the whole recording.
/// </summary>
internal static class TranscriptVtt
{
    public static string FromTalkMarkdown(string markdown, TimeSpan? duration)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var paragraphs = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Talk always appends the warning as the last paragraph.
        if (paragraphs.Count > 0)
        {
            paragraphs.RemoveAt(paragraphs.Count - 1);
        }

        // A cue must end after it starts.
        var end = duration is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(1);
        var vtt = new StringBuilder("WEBVTT\n");
        foreach (var paragraph in paragraphs)
        {
            vtt.Append('\n').Append("00:00:00.000 --> ").Append(Timestamp(end)).Append('\n')
                .Append(Escape(string.Join('\n', paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))).Append('\n');
        }

        return vtt.ToString();
    }

    private static string Timestamp(TimeSpan at) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)at.TotalHours:00}:{at.Minutes:00}:{at.Seconds:00}.{at.Milliseconds:000}");

    // Cue text is markup: escape what would open a tag or an entity (">" also keeps "-->" out).
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
