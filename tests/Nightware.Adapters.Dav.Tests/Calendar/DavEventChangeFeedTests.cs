using Nightware.Adapters.Dav.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

public sealed class DavEventChangeFeedTests : IDisposable
{
    private static readonly DateTimeOffset Since = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
    private readonly CalendarAdapterHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    private DavEventChangeFeed Feed => new(_h.Scopes, _h.Dav.Client, _h.Dav.Paths);

    private void ServeSync(string? from, string responses, string next) =>
        _h.Dav.Server.Given(Request.Create().WithPath(AlicePersonal).UsingMethod("REPORT").WithBody(b => b!.Contains("sync-collection", StringComparison.Ordinal)
                && (from is null ? !b.Contains("sync/", StringComparison.Ordinal) : b.Contains(from, StringComparison.Ordinal))))
            .RespondWith(Response.Create().WithStatusCode(207).WithBody(
                $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:">{responses}<d:sync-token>{next}</d:sync-token></d:multistatus>"""));

    private static string Found(string href) =>
        $"<d:response><d:href>{href}</d:href><d:propstat><d:prop><d:getetag>\"1\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

    private static string Gone(string href) => $"<d:response><d:href>{href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>";

    private static string Event(string uid, string created) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20261121T000000Z\r\nCREATED:{created}\r\nDTSTART:20261122T090000Z\r\nDTEND:20261122T100000Z\r\nSUMMARY:{uid}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public async Task Changes_since_the_baseline_are_events_by_series_created_updated_or_deleted()
    {
        ServeSync(null, Found(AlicePersonal + "old.ics"), "sync/1");
        var baseline = (await Feed.BaselineAsync(AliceMailbox, Ct)).Value;
        ServeSync("sync/1", Found(AlicePersonal + "new.ics") + Found(AlicePersonal + "old.ics") + Gone(AlicePersonal + "gone.ics"), "sync/2");
        _h.ServeIcs(AlicePersonal + "new.ics", Event("new", "20261121T100001Z"));
        _h.ServeIcs(AlicePersonal + "old.ics", Event("old", "20261101T000000Z"));

        baseline.Ids.ShouldBe([_h.IdOf(AlicePersonal + "old.ics").Value], "what exists at the baseline is what the subscription starts out knowing");
        var batch = (await Feed.ChangesAsync(AliceMailbox, baseline.State, Since, Ct)).Value;

        batch.Changes.ShouldBe(
        [
            new(_h.IdOf(AlicePersonal + "new.ics").Value, SubscriptionChangeType.Created),
            new(_h.IdOf(AlicePersonal + "old.ics").Value, SubscriptionChangeType.Updated),
            new(_h.IdOf(AlicePersonal + "gone.ics").Value, SubscriptionChangeType.Deleted),
        ]);
        batch.State.ShouldNotBe(baseline.State);
    }

    [Fact]
    public async Task A_state_that_does_not_decode_is_gone()
    {
        (await Feed.ChangesAsync(AliceMailbox, "!!", Since, Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Theory]
    [InlineData("BEGIN:VEVENT\r\nCREATED:20261121T101112Z\r\nEND:VEVENT", "2026-11-21T10:11:12Z")]
    [InlineData("BEGIN:VEVENT\nCREATED:20261121T101112Z\nEND:VEVENT", "2026-11-21T10:11:12Z")]
    [InlineData("BEGIN:VEVENT\r\nEND:VEVENT", null)]
    public void Created_is_read_from_the_master_vevent(string ics, string? expected) =>
        DavEventChangeFeed.Created(ics).ShouldBe(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
}
