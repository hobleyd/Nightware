using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Calendar;

/// <summary>
/// An answer to an organizer outside Nextcloud that Sabre's iTIP can't carry (a comment, a proposed time) is mailed
/// by the façade as iMIP from the attendee (phase 17). The meeting is put in Bob's calendar over CalDAV, as a client
/// that imported an outside invitation would. The organizer is at example.com: Stalwart drops recipients on reserved
/// test domains (.example, .test) from the envelope.
/// </summary>
public sealed class OutsideOrganizerLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_decline_with_a_comment_goes_to_the_outside_organizer_as_an_imip_reply()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        await using var stack = LiveStack.Start();
        var uid = "p17-outside-" + Guid.NewGuid().ToString("N");
        var day = DateTime.UtcNow.Date.AddDays(40);
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Contoso//Calendar//EN", "BEGIN:VEVENT",
            $"UID:{uid}", $"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}", $"DTSTART:{day.AddHours(1):yyyyMMdd'T'HHmmss'Z'}", $"DTEND:{day.AddHours(2):yyyyMMdd'T'HHmmss'Z'}",
            $"SUMMARY:Vendor review {uid[^6..]}", "SEQUENCE:0", "ORGANIZER;CN=Dana:mailto:dana@example.com",
            $"ATTENDEE;PARTSTAT=NEEDS-ACTION;RSVP=TRUE;CN=Bob:mailto:{DevStack.Address("bob")}", "END:VEVENT", "END:VCALENDAR", string.Empty);
        var href = $"{LiveStack.CloudBase}/remote.php/dav/calendars/bob/personal/{uid}.ics";
        using (var put = new HttpRequestMessage(HttpMethod.Put, href) { Content = new StringContent(ics, Encoding.UTF8, "text/calendar") })
        {
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await stack.NextcloudTokenAsync("bob", Ct));
            using var stored = await stack.Backchannel.SendAsync(put, Ct);
            ((int)stored.StatusCode).ShouldBeOneOf([201, 204], await stored.Content.ReadAsStringAsync(Ct));
        }

        using var bob = await stack.GraphClientAsync("bob", Ct);
        try
        {
            var window = $"startDateTime={day:yyyy-MM-dd}T00:00:00Z&endDateTime={day.AddDays(1):yyyy-MM-dd}T00:00:00Z";
            var events = (await bob.GetFromJsonAsync<JsonObject>($"/v1.0/me/calendarView?{window}&$select=id,subject", Ct))!["value"]!.AsArray();
            var id = events.Select(e => e!.AsObject()).Single(e => e["subject"]!.GetValue<string>().EndsWith(uid[^6..], StringComparison.Ordinal))["id"]!.GetValue<string>();

            using var declined = await bob.PostAsJsonAsync($"/v1.0/me/events/{id}/decline", new { sendResponse = true, comment = "Away that week, sorry" }, Ct);
            declined.StatusCode.ShouldBe(HttpStatusCode.Accepted, await declined.Content.ReadAsStringAsync(Ct));

            // The reply went out from Bob's mailbox: it is in his Sent Items, as text with an iTIP REPLY beside it.
            JsonObject? sent = null;
            for (var attempt = 0; attempt < 20 && sent is null; attempt++)
            {
                await Task.Delay(500, Ct);
                sent = (await bob.GetFromJsonAsync<JsonObject>("/v1.0/me/mailFolders/sentitems/messages?$top=10&$select=id,subject,toRecipients", Ct))!["value"]!.AsArray()
                    .Select(m => m!.AsObject()).FirstOrDefault(m => m["subject"]!.GetValue<string>() == $"Declined: Vendor review {uid[^6..]}");
            }

            sent.ShouldNotBeNull("the iMIP reply is in Bob's Sent Items");
            sent["toRecipients"]![0]!["emailAddress"]!["address"]!.GetValue<string>().ShouldBe("dana@example.com");
            var mime = await bob.GetStringAsync($"/v1.0/me/messages/{sent["id"]!.GetValue<string>()}/$value", Ct);
            mime.ShouldContain("method=REPLY", Case.Insensitive);
            mime.ShouldContain("COMMENT:Away that week");
            mime.ShouldContain($"UID:{uid}");
            mime.ShouldContain("PARTSTAT=DECLINED");
            (await bob.GetFromJsonAsync<JsonObject>($"/v1.0/me/events/{id}?$select=responseStatus", Ct))!["responseStatus"]!["response"]!.GetValue<string>().ShouldBe("declined");
        }
        finally
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, href);
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await stack.NextcloudTokenAsync("bob", Ct));
            using var _ = await stack.Backchannel.SendAsync(delete, Ct);
        }
    }
}
