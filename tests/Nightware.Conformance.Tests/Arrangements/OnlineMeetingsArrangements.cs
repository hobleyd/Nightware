using Nightware.Application.OnlineMeetings;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 26: online meetings with their recordings and transcripts (ADR 0009 D7). The fixtures are
/// constructed from Graph's reference pages with Talk-shaped ids: a meeting is <c>t.</c> plus a room token, and its join
/// link sits on the file site, <c>cloud.example.com</c>.
/// </summary>
public sealed class OnlineMeetingsArrangements : IConformanceArrangements
{
    private const string MeetingId = "t.abcd1234";
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecordingStart = Start.AddSeconds(43);
    private static readonly DateTimeOffset RecordingEnd = Start.AddMinutes(30).AddSeconds(2);

    private static readonly OnlineMeeting Meeting = new(MeetingId, "https://cloud.example.com/cloud/call/abcd1234", "abcd1234")
    {
        Subject = "Q3 review",
        Start = Start,
        End = Start.AddMinutes(30),
        Created = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero),
        JoinMeetingId = "abcd1234",
        Organizer = new MeetingParticipant("me-id", "Me Example", "me@example.com", MeetingRole.Presenter),
        Attendees = [new MeetingParticipant("bob-id", "Bob Brown", "bob@example.com", MeetingRole.Attendee)],
    };

    private static readonly MeetingArtifact Recording = new("445", MeetingArtifactKind.Recording, RecordingStart, RecordingEnd) { ContentCorrelationId = "445" };
    private static readonly MeetingArtifact Transcript = new("446", MeetingArtifactKind.Transcript, RecordingStart, RecordingEnd) { ContentCorrelationId = "445" };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("online-meetings.create", static f => f.For<IOnlineMeetingStore>()
            .CreateAsync(Arg.Is<NewOnlineMeeting>(m => m.Subject == "Q3 review" && m.Start == Start && m.End == Start.AddMinutes(30) && m.Attendees.Single() == "bob-id"), Any)
            .Returns(Meeting));
        yield return new ConformanceArrangement("online-meetings.get", static f => f.For<IOnlineMeetingStore>().GetAsync(MeetingId, Any).Returns(Meeting));
        yield return new ConformanceArrangement("online-meetings.find", static f => f.For<IOnlineMeetingStore>()
            .FindByJoinUrlAsync("https://cloud.example.com/cloud/call/abcd1234", Any).Returns(Meeting));
        yield return new ConformanceArrangement("online-meetings.update", static f => f.For<IOnlineMeetingStore>()
            .UpdateAsync(MeetingId, new OnlineMeetingChange(true, "Patch Meeting Subject", Start.AddDays(1), Start.AddDays(1).AddMinutes(30)), Any)
            .Returns(Meeting with { Subject = "Patch Meeting Subject", Start = null, End = null }));
        yield return new ConformanceArrangement("online-meetings.delete", static f => f.For<IOnlineMeetingStore>().DeleteAsync(MeetingId, Any).Returns(Result.Success()));

        yield return Artifacts("online-meetings.recordings.list", static f => f.For<IMeetingArtifacts>()
            .ListAsync(MeetingId, MeetingArtifactKind.Recording, Any).Returns(Result<IReadOnlyList<MeetingArtifact>>.Success([Recording])));
        yield return Artifacts("online-meetings.recordings.get", static f => f.For<IMeetingArtifacts>()
            .ListAsync(MeetingId, MeetingArtifactKind.Recording, Any).Returns(Result<IReadOnlyList<MeetingArtifact>>.Success([Recording])));
        yield return Artifacts("online-meetings.recordings.content", static f => f.For<IMeetingArtifacts>()
            .OpenAsync(MeetingId, MeetingArtifactKind.Recording, "445", Any).Returns(_ => Blob("audio/ogg", [.. "OggS"u8, 0, 2])));
        yield return Artifacts("online-meetings.transcripts.list", static f => f.For<IMeetingArtifacts>()
            .ListAsync(MeetingId, MeetingArtifactKind.Transcript, Any).Returns(Result<IReadOnlyList<MeetingArtifact>>.Success([Transcript])));
        yield return Artifacts("online-meetings.transcripts.get", static f => f.For<IMeetingArtifacts>()
            .ListAsync(MeetingId, MeetingArtifactKind.Transcript, Any).Returns(Result<IReadOnlyList<MeetingArtifact>>.Success([Transcript])));
        yield return Artifacts("online-meetings.transcripts.content", static f => f.For<IMeetingArtifacts>()
            .OpenAsync(MeetingId, MeetingArtifactKind.Transcript, "446", Any).Returns(_ => Blob("text/vtt", "WEBVTT\n\n00:00:00.000 --> 00:29:19.000\nThis is a transcript test.\n"u8.ToArray())));
    }

    private static CancellationToken Any => Arg.Any<CancellationToken>();

    /// <summary>Recordings and transcripts are read with their meeting, which Graph names as their organizer.</summary>
    private static ConformanceArrangement Artifacts(string operation, Action<PortFakes> arrange) =>
        new(operation, fakes =>
        {
            fakes.For<IOnlineMeetingStore>().GetAsync(MeetingId, Any).Returns(Meeting);
            arrange(fakes);
        });

    private static BlobContent Blob(string type, byte[] bytes) => new(type, bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes)));
}
