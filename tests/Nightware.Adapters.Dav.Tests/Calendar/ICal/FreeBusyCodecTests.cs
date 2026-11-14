using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Domain.Calendar;

namespace Nightware.Adapters.Dav.Tests.Calendar.ICal;

public sealed class FreeBusyCodecTests
{
    [Fact]
    public void Builds_a_vfreebusy_request()
    {
        var codec = new FreeBusyCodec(new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 1, 2, 3, TimeSpan.Zero)));
        var ics = codec.BuildRequest("alice@example.com", ["bob@example.com", " "], new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.FromHours(11)), new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), "u1");
        ics.ShouldBe(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Nightware//Nightware//EN\r\nMETHOD:REQUEST\r\nBEGIN:VFREEBUSY\r\nUID:u1\r\n" +
            "DTSTAMP:20261115T010203Z\r\nDTSTART:20261115T220000Z\r\nDTEND:20261117T000000Z\r\nORGANIZER:mailto:alice@example.com\r\n" +
            "ATTENDEE:mailto:bob@example.com\r\nEND:VFREEBUSY\r\nEND:VCALENDAR\r\n");
    }

    [Fact]
    public void Parses_fbtypes_durations_and_period_lists()
    {
        var items = FreeBusyCodec.ParseReply(Fixture.Read("vfreebusy-query.ics"));
        items.Select(i => i.Status).ShouldBe([FreeBusyStatus.Busy, FreeBusyStatus.Tentative, FreeBusyStatus.Oof, FreeBusyStatus.Oof]);
        items[1].End.ShouldBe(new DateTimeOffset(2026, 11, 16, 3, 30, 0, TimeSpan.Zero));
        items[3].Start.ShouldBe(new DateTimeOffset(2026, 11, 16, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Ignores_empty_and_freebusy_outside_vfreebusy()
    {
        FreeBusyCodec.ParseReply(null).ShouldBeEmpty();
        FreeBusyCodec.ParseReply("BEGIN:VCALENDAR\r\nFREEBUSY:20261116T010000Z/20261116T020000Z\r\nEND:VCALENDAR\r\n").ShouldBeEmpty();
        FreeBusyCodec.ParseReply("BEGIN:VFREEBUSY\r\nFREEBUSY;FBTYPE=FREE:20261116T010000Z/20261116T02\r\n 0000Z,bad\r\nEND:VFREEBUSY\r\n")
            .ShouldHaveSingleItem().Status.ShouldBe(FreeBusyStatus.Free);
    }
}
