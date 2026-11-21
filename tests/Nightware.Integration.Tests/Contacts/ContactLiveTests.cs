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

    /// <summary>A contact id is the card's DAV href, base64url-encoded behind an "r:" kind marker.</summary>
    private static string CardUrl(string contactId)
    {
        var padded = contactId.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return $"{LiveStack.CloudBase}/remote.php/dav/{Encoding.UTF8.GetString(Convert.FromBase64String(padded))["r:".Length..]}";
    }

    /// <summary>The card as Nextcloud stores it, read straight over CardDAV.</summary>
    private async Task<string> RawCardAsync(string contactId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CardUrl(contactId));
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

    [Fact]
    public async Task A_folder_is_an_address_book_that_holds_its_own_contacts()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var defaultFolder = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/contactFolders/contacts");
        defaultFolder["wellKnownName"]!.GetValue<string>().ShouldBe("contacts");

        var folder = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/contactFolders", $$"""{"displayName":"Live {{_run}}"}""", HttpStatusCode.Created);
        var folderId = folder["id"]!.GetValue<string>();
        try
        {
            folder["parentFolderId"]!.GetValue<string>().ShouldBe(defaultFolder["id"]!.GetValue<string>());
            (await SendAsync(alice, HttpMethod.Post, "/v1.0/me/contactFolders", $$"""{"displayName":"live {{_run}}"}""", HttpStatusCode.Conflict)).ShouldNotBeNull();
            (await SendAsync(alice, HttpMethod.Get, "/v1.0/me/contactFolders"))["value"]!.AsArray()
                .ShouldContain(f => f!["id"]!.GetValue<string>() == folderId);

            var renamed = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/contactFolders/{folderId}", $$"""{"displayName":"Live {{_run}} renamed"}""");
            renamed["displayName"]!.GetValue<string>().ShouldBe($"Live {_run} renamed");
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/contactFolders/{folderId}"))["displayName"]!.GetValue<string>().ShouldBe($"Live {_run} renamed");

            var sam = await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/contactFolders/{folderId}/contacts", """{"givenName":"Sam","surname":"Folder"}""", HttpStatusCode.Created);
            sam["parentFolderId"]!.GetValue<string>().ShouldBe(folderId);
            var inFolder = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/contactFolders/{folderId}/contacts");
            inFolder["value"]!.AsArray().ShouldHaveSingleItem()!["displayName"]!.GetValue<string>().ShouldBe("Sam Folder");
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/contactFolders/{folderId}/childFolders"))["value"]!.AsArray().ShouldBeEmpty();

            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/contactFolders/{defaultFolder["id"]!.GetValue<string>()}", expected: HttpStatusCode.BadRequest);
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/contactFolders/{folderId}", expected: HttpStatusCode.NoContent);
        }

        await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/contactFolders/{folderId}", expected: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_edit_made_in_nextcloud_shows_in_the_folders_delta()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var created = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/contacts", $$"""{"givenName":"Delta","surname":"{{_run}}"}""", HttpStatusCode.Created);
        var id = created["id"]!.GetValue<string>();
        var deleted = false;
        try
        {
            var initial = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/contactFolders/contacts/contacts/delta");
            initial["value"]!.AsArray().ShouldContain(c => c!["id"]!.GetValue<string>() == id);
            var link = LiveStack.Local(initial["@odata.deltaLink"]!.GetValue<string>());

            // The edit is Nextcloud's own (as from its Contacts app), not the façade's.
            var card = (await RawCardAsync(id)).Replace($"FN:Delta {_run}", $"FN:Delta {_run} edited", StringComparison.Ordinal);
            using (var put = new HttpRequestMessage(HttpMethod.Put, CardUrl(id)) { Content = new StringContent(card, Encoding.UTF8, "text/vcard") })
            {
                put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _stack.NextcloudTokenAsync(Alice, Ct));
                put.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
                using var response = await _stack.Backchannel.SendAsync(put, Ct);
                response.IsSuccessStatusCode.ShouldBeTrue();
            }

            var changes = await SendAsync(alice, HttpMethod.Get, link);
            // Other live tests share alice's default book, so other cards may appear too.
            changes["value"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == id)!["displayName"]!.GetValue<string>().ShouldBe($"Delta {_run} edited");

            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/contacts/{id}", expected: HttpStatusCode.NoContent);
            deleted = true;
            var removal = await SendAsync(alice, HttpMethod.Get, LiveStack.Local(changes["@odata.deltaLink"]!.GetValue<string>()));
            var gone = removal["value"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == id)!;
            gone["@removed"]!["reason"]!.GetValue<string>().ShouldBe("deleted");
        }
        finally
        {
            if (!deleted)
            {
                await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/contacts/{id}", expected: HttpStatusCode.NoContent);
            }
        }
    }

    [Fact]
    public async Task Folder_delta_reports_a_new_folder_and_its_removal()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var initial = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/contactFolders/delta");
        var folder = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/contactFolders", $$"""{"displayName":"Delta {{_run}}"}""", HttpStatusCode.Created);
        var folderId = folder["id"]!.GetValue<string>();
        JsonObject added;
        try
        {
            added = await SendAsync(alice, HttpMethod.Get, LiveStack.Local(initial["@odata.deltaLink"]!.GetValue<string>()));
            added["value"]!.AsArray().ShouldContain(f => f!["id"]!.GetValue<string>() == folderId);
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/contactFolders/{folderId}", expected: HttpStatusCode.NoContent);
        }

        var removed = await SendAsync(alice, HttpMethod.Get, LiveStack.Local(added["@odata.deltaLink"]!.GetValue<string>()));
        removed["value"]!.AsArray().Single(f => f!["id"]!.GetValue<string>() == folderId)!["@removed"].ShouldNotBeNull();
    }
}
