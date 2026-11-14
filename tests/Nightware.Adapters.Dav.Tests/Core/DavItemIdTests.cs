using System.Buffers.Text;
using System.Text;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Core;

public sealed class DavItemIdTests
{
    private const string Href = "calendars/alice/personal/abc%40def.ics";

    private static string Encode(string payload) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));

    [Fact]
    public void Resource_round_trips()
    {
        var id = DavItemId.Resource(Href);
        var text = id.ToString();

        DavItemId.TryParse(text, out var parsed).ShouldBeTrue();
        parsed!.Value.Kind.ShouldBe(DavItemKind.Resource);
        parsed.Value.Href.ShouldBe(Href);
        parsed.Value.RecurrenceId.ShouldBeNull();
        parsed.Value.ShouldBe(id);
    }

    [Fact]
    public void Ids_are_url_safe_and_unpadded()
    {
        var text = DavItemId.Occurrence("calendars/alice/personal/ü??x.ics".Replace("??", "~~", StringComparison.Ordinal), DavRecurrenceId.FromUtc(DateTimeOffset.UnixEpoch)).ToString();
        text.ShouldNotContain("+");
        text.ShouldNotContain("/");
        text.ShouldNotContain("=");
    }

    [Fact]
    public void Resource_payload_is_versioned()
    {
        DavItemId.Resource(Href).ToString().ShouldBe(Encode("r:" + Href));
    }

    [Fact]
    public void Occurrence_round_trips_utc_recurrence_id()
    {
        var rid = DavRecurrenceId.FromUtc(new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.FromHours(11)));
        var id = DavItemId.Occurrence(Href, rid);

        id.ToString().ShouldBe(Encode("o:20261115T223000Z:" + Href));
        DavItemId.TryParse(id.ToString(), out var parsed).ShouldBeTrue();
        parsed!.Value.IsOccurrence.ShouldBeTrue();
        parsed.Value.Href.ShouldBe(Href);
        parsed.Value.RecurrenceId!.Value.Value.ShouldBe("20261115T223000Z");
        parsed.Value.RecurrenceId!.Value.ToUtc().ShouldBe(new DateTimeOffset(2026, 11, 15, 22, 30, 0, TimeSpan.Zero));
        parsed.Value.Master.ShouldBe(DavItemId.Resource(Href));
    }

    [Fact]
    public void Occurrence_round_trips_all_day_recurrence_id()
    {
        var id = DavItemId.Occurrence(Href, DavRecurrenceId.FromDate(new DateOnly(2026, 12, 25)));

        DavItemId.TryParse(id.ToString(), out var parsed).ShouldBeTrue();
        parsed!.Value.RecurrenceId!.Value.IsDate.ShouldBeTrue();
        parsed.Value.RecurrenceId!.Value.ToDate().ShouldBe(new DateOnly(2026, 12, 25));
    }

    [Theory]
    [InlineData("r:../../etc/passwd")]
    [InlineData("r:calendars/../../../config/config.php")]
    [InlineData("r:calendars/alice/%2e%2e/%2E%2E/x")]
    [InlineData("r:/remote.php/dav/calendars/alice/x.ics")]
    [InlineData("r://evil.example/x")]
    [InlineData("r:https://evil.example/x")]
    [InlineData("r:calendars\\alice\\x.ics")]
    [InlineData("r:calendars/alice/x.ics?download")]
    [InlineData("r:calendars/alice/x.ics#frag")]
    [InlineData("r:calendars//x.ics")]
    [InlineData("r:calendars/./x.ics")]
    [InlineData("r:calendars/alice/x\u0000.ics")]
    [InlineData("r:calendars/alice/%00.ics")]
    [InlineData("r:")]
    [InlineData("x:calendars/alice/x.ics")]
    [InlineData("calendars/alice/x.ics")]
    [InlineData("o:2026-11-15:calendars/alice/x.ics")]
    [InlineData("o:20261115T093000:calendars/alice/x.ics")]
    [InlineData("o:20261315T093000Z:calendars/alice/x.ics")]
    [InlineData("o:20261115T093000Z")]
    [InlineData("o:20261115T093000Z:../x.ics")]
    public void Tampered_payloads_are_rejected(string payload)
    {
        DavItemId.TryParse(Encode(payload), out var parsed).ShouldBeFalse();
        parsed.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("cjpjYWxlbmRhcnMvYWxpY2UveC5pY3M=")] // padded: not canonical
    public void Malformed_strings_are_rejected(string? value) =>
        DavItemId.TryParse(value, out _).ShouldBeFalse();

    [Fact]
    public void Invalid_utf8_is_rejected() =>
        DavItemId.TryParse(Base64Url.EncodeToString([(byte)'r', (byte)':', 0xC3, 0x28]), out _).ShouldBeFalse();

    [Fact]
    public void Overlong_ids_are_rejected() =>
        DavItemId.TryParse(new string('A', DavItemId.MaxEncodedLength + 1), out _).ShouldBeFalse();

    [Fact]
    public void Parse_reports_not_found_for_bad_ids()
    {
        var result = DavItemId.Parse("garbage", "id");
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        result.Error.Target.ShouldBe("id");
    }

    [Fact]
    public void Factories_reject_unsafe_hrefs()
    {
        Should.Throw<ArgumentException>(() => DavItemId.Resource("../x"));
        Should.Throw<ArgumentException>(() => DavItemId.Occurrence("/abs/x", DavRecurrenceId.FromDate(new DateOnly(2026, 1, 1))));
    }

    [Fact]
    public void IsWithin_checks_the_collection_prefix()
    {
        var id = DavItemId.Resource("calendars/alice/personal/x.ics");
        id.IsWithin("calendars/alice/personal/").ShouldBeTrue();
        id.IsWithin("calendars/alice/personal").ShouldBeTrue();
        id.IsWithin("calendars/alice/person").ShouldBeFalse();
        id.IsWithin("calendars/bob/").ShouldBeFalse();
    }

    [Fact]
    public void Domain_id_conversions_round_trip()
    {
        var id = DavItemId.Resource(Href);
        id.ToEventId().ToDavItemId().Value.ShouldBe(id);
        id.ToTaskId().ToDavItemId().Value.ShouldBe(id);
        id.ToTaskListId().ToDavItemId().Value.ShouldBe(id);
        id.ToContactId().ToDavItemId().Value.ShouldBe(id);
        new EventId("bogus").ToDavItemId().IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("20261115T093000Z", true)]
    [InlineData("20261115", true)]
    [InlineData("20261115T093000", false)]
    [InlineData("2026111", false)]
    [InlineData("20260230", false)]
    [InlineData(null, false)]
    public void Recurrence_id_parsing(string? value, bool valid)
    {
        DavRecurrenceId.TryParse(value, out var rid).ShouldBe(valid);
        if (valid)
        {
            rid.Value.ShouldBe(value);
            rid.ToString().ShouldBe(value);
        }
    }

    [Fact]
    public void All_day_recurrence_id_instant_is_midnight_utc() =>
        DavRecurrenceId.FromDate(new DateOnly(2026, 3, 1)).ToUtc().ShouldBe(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
}
