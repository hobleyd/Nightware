using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Contacts;

/// <summary>
/// Phase 14 contacts against the running stack: a contact written over Graph lands in Nextcloud as a vCard
/// carrying every mapped field, PATCH clears with null, the photo round-trips, and DELETE removes the card.
/// </summary>
public sealed class ContactLiveTests : IAsyncLifetime
{
    private const string Alice = "alice";
    private LiveStack _stack = null!;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _stack = LiveStack.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stack is not null)
        {
            await _stack.DisposeAsync();
        }
    }

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string url, string? json = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>The card as Nextcloud stores it, read straight over CardDAV.</summary>
    private async Task<string> RawCardAsync(string contactId)
    {
        // A contact id is the card's DAV href, base64url-encoded behind an "r:" kind marker.
        var padded = contactId.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        var href = Encoding.UTF8.GetString(Convert.FromBase64String(padded))["r:".Length..];
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{LiveStack.CloudBase}/remote.php/dav/{href}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _stack.NextcloudTokenAsync(Alice, Ct));
        request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
        using var response = await _stack.Backchannel.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadAsStringAsync(Ct)).Replace("\r\n ", string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_contact_written_over_graph_is_a_full_vcard_in_nextcloud()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var created = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/contacts", $$"""
            {"givenName":"Pat","surname":"Live {{_run}}","middleName":"Q","title":"Dr","generation":"Jr","nickName":"Patty",
             "jobTitle":"Engineer","department":"R&D","companyName":"Example Ltd","profession":"Boffin","officeLocation":"Level 3",
             "assistantName":"Ann","manager":"Max","spouseName":"Sam","children":["Kid"],"initials":"P.Q.","fileAs":"Live, Pat",
             "yomiGivenName":"Patto","yomiSurname":"Raibu","yomiCompanyName":"Ekzampl",
             "emailAddresses":[{"name":"Pat at work","address":"pat.{{_run}}@example.com"}],
             "businessPhones":["+61 2 5550 0100"],"homePhones":["+61 2 5550 0101"],"mobilePhone":"+61 400 555 010",
             "businessAddress":{"street":"1 Example St","city":"Sydney","state":"NSW","postalCode":"2000","countryOrRegion":"Australia"},
             "homeAddress":{"street":"2 Sample Rd","city":"Sydney","postalCode":"2001"},
             "otherAddress":{"street":"3 Other St","city":"Sydney"},
             "birthday":"1990-05-17T00:00:00Z","personalNotes":"Line one\nLine two","imAddresses":["pat@im.example.com"],
             "categories":["Nightware {{_run}}"],"businessHomePage":"https://example.com/pat"}
            """, HttpStatusCode.Created);
        var id = created["id"]!.GetValue<string>();
        try
        {
            created["displayName"]!.GetValue<string>().ShouldBe($"Dr Pat Q Live {_run} Jr");
            created["birthday"]!.GetValue<string>().ShouldBe("1990-05-17T11:59:00Z");
            created["parentFolderId"].ShouldNotBeNull();

            var card = await RawCardAsync(id);
            foreach (var line in new[]
            {
                $"N:Live {_run};Pat;Q;Dr;Jr", "NICKNAME:Patty", "ORG:Example Ltd;R&D", "TITLE:Engineer", "ROLE:Boffin",
                "X-ASSISTANT:Ann", "X-MANAGER:Max", "RELATED;TYPE=spouse;VALUE=text:Sam", "RELATED;TYPE=child;VALUE=text:Kid",
                "X-PHONETIC-FIRST-NAME:Patto", "X-NIGHTWARE-OFFICE-LOCATION:Level 3", "X-NIGHTWARE-FILE-AS:Live\\, Pat",
                "X-NAME=Pat at work", "IMPP:pat@im.example.com", "URL;TYPE=WORK:https://example.com/pat", $"CATEGORIES:Nightware {_run}",
                "ADR;TYPE=OTHER:;;3 Other St;Sydney;;;", "BDAY:1990-05-17", "NOTE:Line one\\nLine two",
            })
            {
                card.ShouldContain(line, Case.Sensitive, $"the stored card:\n{card}");
            }

            var read = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/contacts/{id}");
            read["otherAddress"]!["street"]!.GetValue<string>().ShouldBe("3 Other St");
            read["children"]![0]!.GetValue<string>().ShouldBe("Kid");
            read["emailAddresses"]![0]!["name"]!.GetValue<string>().ShouldBe("Pat at work");

            var patched = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/contacts/{id}", """{"companyName":"Example Pty Ltd","department":null,"otherAddress":null,"birthday":null}""");
            patched["companyName"]!.GetValue<string>().ShouldBe("Example Pty Ltd");
            patched["department"].ShouldBeNull();
            patched["otherAddress"]!.AsObject().Count.ShouldBe(0);
            patched["birthday"].ShouldBeNull();
            patched["jobTitle"]!.GetValue<string>().ShouldBe("Engineer", "what the patch did not name is kept");

            byte[] jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAAA//EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AN//Z");
            using (var put = new HttpRequestMessage(HttpMethod.Put, $"/v1.0/me/contacts/{id}/photo/$value") { Content = new ByteArrayContent(jpeg) })
            {
                put.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                using var response = await alice.SendAsync(put, Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            }

            using (var photo = await alice.GetAsync($"/v1.0/me/contacts/{id}/photo/$value", Ct))
            {
                photo.StatusCode.ShouldBe(HttpStatusCode.OK);
                photo.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
                (await photo.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(jpeg);
            }

            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/contacts/{id}"))["companyName"]!.GetValue<string>().ShouldBe("Example Pty Ltd", "the photo write kept the fields");
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/contacts/{id}", expected: HttpStatusCode.NoContent);
        }

        await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/contacts/{id}", expected: HttpStatusCode.NotFound);
    }
}
