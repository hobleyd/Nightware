using Nightware.Adapters.Dav.OnlineMeetings;

namespace Nightware.Adapters.Dav.Tests.OnlineMeetings;

public sealed class TranscriptVttTests
{
    [Fact]
    public void Each_paragraph_is_a_cue_spanning_the_recording_and_the_warning_goes()
    {
        var vtt = TranscriptVtt.FromTalkMarkdown("Good morning team.\r\n\r\nThe review moved\nto Thursday.\n\nTranscript is AI generated and may contain mistakes\n", TimeSpan.FromMinutes(75.5));

        vtt.ShouldBe("WEBVTT\n\n00:00:00.000 --> 01:15:30.000\nGood morning team.\n\n00:00:00.000 --> 01:15:30.000\nThe review moved\nto Thursday.\n");
    }

    [Fact]
    public void An_empty_transcript_has_no_cues()
    {
        TranscriptVtt.FromTalkMarkdown("\n\nTranscript is AI generated and may contain mistakes\n", TimeSpan.FromSeconds(19)).ShouldBe("WEBVTT\n");
        TranscriptVtt.FromTalkMarkdown(string.Empty, null).ShouldBe("WEBVTT\n");
    }

    [Fact]
    public void Markup_is_escaped_and_an_unknown_duration_is_a_second()
    {
        TranscriptVtt.FromTalkMarkdown("a <b> & c --> d\n\nwarning", null).ShouldBe("WEBVTT\n\n00:00:00.000 --> 00:00:01.000\na &lt;b&gt; &amp; c --&gt; d\n");
    }
}
