using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Calendar.ICal;

/// <summary>
/// A Talk join link in <c>LOCATION</c> as well as <c>URL</c> (ADR 0009 D7): written after the place so Talk follows the event,
/// never shown in Graph's <c>location</c>.
/// </summary>
public sealed class MeetingLocationTests
{
    private const string Link = "https://cloud.example.com/cloud/call/pv8f59mw";
    private const string Alice = "alice@example.com";

    private readonly IcalNetCalendarCodec _codec = new(new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)));
    private readonly IcsEditor _editor = new(new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)));

    private static string Unfolded(string ics) => ics.Replace("\r\n ", string.Empty, StringComparison.Ordinal);

    [Theory]
    [InlineData(Link, true)]
    [InlineData("http://cloud.example.com/call/abcd", true)]
    [InlineData(Link + "/", true)]
    [InlineData(Link + "?x=1", false)]
    [InlineData(Link + "#x", false)]
    [InlineData("https://cloud.example.com/cloud/call/NOT-A-TOKEN", false)]
    [InlineData("https://cloud.example.com/cloud/calls/pv8f59mw", false)]
    [InlineData("https://teams.microsoft.com/l/meetup-join/abc", false)]
    [InlineData("ftp://cloud.example.com/call/pv8f59mw", false)]
    public void Only_a_talk_call_link_takes_part(string url, bool isLink) =>
        MeetingLocation.IsCallLink(new Uri(url)).ShouldBe(isLink);

    [Theory]
    [InlineData("Boardroom, " + Link, "Boardroom")]
    [InlineData("Boardroom; " + Link, "Boardroom")]
    [InlineData("Boardroom " + Link, "Boardroom")]
    [InlineData(Link, null)]
    [InlineData("  " + Link + "  ", null)]
    [InlineData("Boardroom", "Boardroom")]
    [InlineData("x" + Link, "x" + Link)]
    [InlineData("", null)]
    public void Strip_removes_only_the_events_own_trailing_link(string location, string? shown) =>
        MeetingLocation.Strip(location, Link).ShouldBe(shown);

    [Theory]
    [InlineData(null, Link)]
    [InlineData(" ", Link)]
    [InlineData("Boardroom", "Boardroom, " + Link)]
    [InlineData("Boardroom, " + Link, "Boardroom, " + Link)]
    public void With_puts_the_link_after_the_place_once(string? location, string stored) =>
        MeetingLocation.With(location, Link).ShouldBe(stored);

    [Fact]
    public void A_new_online_event_carries_the_link_after_its_place_and_reads_back_without_it()
    {
        var ev = new CalendarEvent(new EventId(string.Empty), "Review", new DateTimeTimeZone(new DateTime(2026, 12, 1, 9, 0, 0), "UTC"), new DateTimeTimeZone(new DateTime(2026, 12, 1, 10, 0, 0), "UTC"))
        {
            ICalUId = "online-1",
            Organizer = new EmailAddress(Alice),
            Attendees = [new Attendee(new EmailAddress("boardroom@example.com", "Boardroom"), AttendeeType.Resource, new ResponseStatus(ResponseType.None))],
            Location = new Location("Boardroom") { LocationEmailAddress = "boardroom@example.com" },
            OnlineMeetingUrl = Link,
            IsOnlineMeeting = true,
        };

        var ics = Unfolded(_codec.Serialize(new CalendarDocument(null, [ev])));

        ics.ShouldContain(@"LOCATION:Boardroom\, " + Link);
        ics.ShouldContain("URL:" + Link);
        var back = _codec.Parse(ics).Value.Events.Single();
        back.Location!.DisplayName.ShouldBe("Boardroom");
        back.Location.LocationEmailAddress.ShouldBe("boardroom@example.com"); // the room still matches its name
        back.Locations.Select(l => l.DisplayName).ShouldBe(["Boardroom"]);
        back.OnlineMeetingUrl.ShouldBe(Link);
    }

    [Fact]
    public void An_online_event_without_a_place_stores_the_link_alone_and_reads_no_location()
    {
        var ics = _codec.Serialize(new CalendarDocument(null, [Plain() with { OnlineMeetingUrl = Link, IsOnlineMeeting = true }]));

        Unfolded(ics).ShouldContain("LOCATION:" + Link);
        var back = _codec.Parse(ics).Value.Events.Single();
        back.Location.ShouldBeNull();
        back.Locations.ShouldBeEmpty();
    }

    [Fact]
    public void Another_meeting_link_leaves_location_as_it_is()
    {
        var other = "https://meet.example.com/xyz";
        var ics = _codec.Serialize(new CalendarDocument(null, [Plain() with { Location = new Location(other), OnlineMeetingUrl = other, IsOnlineMeeting = true }]));

        Unfolded(ics).ShouldContain("LOCATION:" + other);
        _codec.Parse(ics).Value.Events.Single().Location!.DisplayName.ShouldBe(other);
    }

    [Fact]
    public void An_invitation_mailed_with_the_link_in_location_reads_without_it()
    {
        var invitation = Stored(location: @"Boardroom\, " + Link, organizer: "bob@example.com").Replace("BEGIN:VCALENDAR\r\n", "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\n", StringComparison.Ordinal);

        var ev = _codec.Parse(invitation).Value.Events.Single();

        ev.Location!.DisplayName.ShouldBe("Boardroom");
        ev.OnlineMeetingUrl.ShouldBe(Link);
    }

    [Fact]
    public void A_new_place_keeps_the_link_after_it_and_clearing_the_place_leaves_the_link()
    {
        var moved = Unfolded(_editor.ApplyPatch(Stored(location: @"Boardroom\, " + Link), new EventPatch { Location = new Location("Room 2") }, null, Alice).Value);
        moved.ShouldContain(@"LOCATION:Room 2\, " + Link);

        var cleared = Unfolded(_editor.ApplyPatch(moved, new EventPatch { Location = new Location(string.Empty) }, null, Alice).Value);
        cleared.ShouldContain("LOCATION:" + Link + "\r\n");

        // A client that sends the place back as read (without the link), or with it, gets it once.
        var again = Unfolded(_editor.ApplyPatch(moved, new EventPatch { Location = new Location("Room 2, " + Link) }, null, Alice).Value);
        again.ShouldContain(@"LOCATION:Room 2\, " + Link + "\r\n");
    }

    [Fact]
    public void An_event_with_the_link_in_url_alone_gains_it_with_a_change_of_subject_time_or_link_only()
    {
        var legacy = Stored(location: "Boardroom");

        Unfolded(_editor.ApplyPatch(legacy, new EventPatch { Categories = ["Work"] }, null, Alice).Value).ShouldContain("LOCATION:Boardroom\r\n");
        Unfolded(_editor.ApplyPatch(legacy, new EventPatch { Subject = "Renamed" }, null, Alice).Value).ShouldContain(@"LOCATION:Boardroom\, " + Link);
        Unfolded(_editor.ApplyPatch(legacy, new EventPatch
        {
            Start = new DateTimeTimeZone(new DateTime(2026, 12, 2, 9, 0, 0), "UTC"),
            End = new DateTimeTimeZone(new DateTime(2026, 12, 2, 10, 0, 0), "UTC"),
        }, null, Alice).Value).ShouldContain(@"LOCATION:Boardroom\, " + Link);
    }

    [Fact]
    public void Turning_an_event_online_puts_the_new_link_in_location()
    {
        var offline = Stored(location: "Boardroom").Replace("URL:" + Link + "\r\n", string.Empty, StringComparison.Ordinal);

        var online = Unfolded(_editor.ApplyPatch(offline, new EventPatch { IsOnlineMeeting = true, OnlineMeetingUrl = Link }, null, Alice).Value);

        online.ShouldContain(@"LOCATION:Boardroom\, " + Link);
        online.ShouldContain("URL:" + Link);
    }

    [Fact]
    public void An_event_talk_would_fail_on_gets_a_dtend_and_loses_an_empty_summary()
    {
        // Talk's listener answers 500 to a PUT without DTEND or with an empty SUMMARY (Talk 25.0.8, probed).
        var stored = Stored(location: "Boardroom").Replace("DTEND:20261201T100000Z", "DURATION:PT2H", StringComparison.Ordinal)
            .Replace("SUMMARY:Review", "SUMMARY:", StringComparison.Ordinal);

        var edited = Unfolded(_editor.ApplyPatch(stored, new EventPatch { Location = new Location("Room 2") }, null, Alice).Value);

        edited.ShouldContain("DTEND:20261201T110000Z");
        edited.ShouldNotContain("DURATION");
        edited.ShouldNotContain("SUMMARY");
        edited.ShouldContain(@"LOCATION:Room 2\, " + Link);
    }

    [Fact]
    public void A_new_online_event_without_a_subject_has_no_summary()
    {
        var ics = _codec.Serialize(new CalendarDocument(null, [Plain() with { Subject = string.Empty, OnlineMeetingUrl = Link, IsOnlineMeeting = true }]));

        ics.ShouldNotContain("SUMMARY");
        ics.ShouldContain("DTEND:");
    }

    [Fact]
    public void An_attendees_copy_is_left_as_the_organizer_sent_it()
    {
        var copy = Stored(location: "Boardroom", organizer: "bob@example.com");

        Unfolded(_editor.ApplyPatch(copy, new EventPatch { Subject = "Mine" }, null, Alice).Value).ShouldContain("LOCATION:Boardroom\r\n");
    }

    private static CalendarEvent Plain() =>
        new(new EventId(string.Empty), "Review", new DateTimeTimeZone(new DateTime(2026, 12, 1, 9, 0, 0), "UTC"), new DateTimeTimeZone(new DateTime(2026, 12, 1, 10, 0, 0), "UTC"))
        {
            ICalUId = "plain-1",
        };

    /// <summary>A stored single meeting with bob, organized by <paramref name="organizer"/>, its link in URL.</summary>
    private static string Stored(string location, string organizer = Alice) =>
        string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//test//EN", "BEGIN:VEVENT", "UID:stored-1", "DTSTAMP:20261120T000000Z",
            "DTSTART:20261201T090000Z", "DTEND:20261201T100000Z", "SUMMARY:Review", "SEQUENCE:0", "LOCATION:" + location, "URL:" + Link,
            "ORGANIZER:mailto:" + organizer, "ATTENDEE;PARTSTAT=ACCEPTED:mailto:" + organizer,
            "ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:" + (organizer == Alice ? "bob@example.com" : Alice),
            "END:VEVENT", "END:VCALENDAR", string.Empty);
}
