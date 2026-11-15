using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Nightware.Integration.Tests.Directory;

/// <summary>Phase 5A against the running stack: /me, /users, contacts, people and photos for alice and bob.</summary>
public sealed class DirectoryLiveTests
{
    private const string Alice = "alice@nightware.localhost";
    private const string Bob = "bob@nightware.localhost";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> GetJsonAsync(HttpClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(path, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>Follows nextLinks the way the client does (absolute, verbatim, host-checked).</summary>
    private static async Task<List<JsonObject>> WalkAsync(HttpClient client, string path)
    {
        var items = new List<JsonObject>();
        for (string? next = path; next is not null;)
        {
            var page = await GetJsonAsync(client, next);
            items.AddRange(page["value"]!.AsArray().Select(static v => v!.AsObject()));
            next = page["@odata.nextLink"]?.GetValue<string>() is { } link ? LiveStack.Local(link) : null;
            items.Count.ShouldBeLessThan(10_000);
        }

        return items;
    }

    [Fact]
    public async Task Me_for_alice_comes_from_keycloak()
    {
        await using var stack = LiveStack.Start();
        using var client = await stack.GraphClientAsync("alice", Ct);

        var me = await GetJsonAsync(client, "/v1.0/me?$select=id,displayName,mail,userPrincipalName,proxyAddresses");

        me["displayName"]!.GetValue<string>().ShouldBe("Alice Anderson");
        me["mail"]!.GetValue<string>().ShouldBe(Alice);
        me["userPrincipalName"]!.GetValue<string>().ShouldBe(Alice);
        me["proxyAddresses"]!.AsArray().Select(static p => p!.GetValue<string>()).ShouldContain("SMTP:" + Alice);
        Guid.TryParse(me["id"]!.GetValue<string>(), out _).ShouldBeTrue("the Keycloak id");
    }

    [Fact]
    public async Task Users_walk_pages_on_the_graph_base_and_finds_alice_and_bob()
    {
        await using var stack = LiveStack.Start();
        using var client = await stack.GraphClientAsync("alice", Ct);

        var users = await WalkAsync(client, "/v1.0/users?$select=displayName,mail,userPrincipalName&$top=1");

        users.Select(static u => u["mail"]?.GetValue<string>()).ShouldContain(Alice);
        users.Select(static u => u["mail"]?.GetValue<string>()).ShouldContain(Bob);
        users.ShouldAllBe(static u => !u.ContainsKey("id"), "id was not selected");
        users.Select(static u => u["userPrincipalName"]!.GetValue<string>()).ShouldAllBe(static upn => upn.Contains('@'));
    }

    [Fact]
    public async Task Bob_is_found_by_address_in_both_encodings_and_by_id()
    {
        await using var stack = LiveStack.Start();
        using var client = await stack.GraphClientAsync("alice", Ct);
        const string select = "?$select=id,displayName,jobTitle,department,companyName,mobilePhone,businessPhones,officeLocation,mail,userPrincipalName";

        var encoded = await GetJsonAsync(client, "/v1.0/users/bob%40nightware.localhost" + select);
        var raw = await GetJsonAsync(client, "/v1.0/users/" + Bob + select);
        var byId = await GetJsonAsync(client, "/v1.0/users/" + encoded["id"]!.GetValue<string>() + select);

        encoded["displayName"]!.GetValue<string>().ShouldBe("Bob Brown");
        encoded["mail"]!.GetValue<string>().ShouldBe(Bob);
        raw["id"]!.GetValue<string>().ShouldBe(encoded["id"]!.GetValue<string>());
        byId["mail"]!.GetValue<string>().ShouldBe(Bob);
    }

    [Fact]
    public async Task An_unknown_user_is_a_silent_404()
    {
        await using var stack = LiveStack.Start();
        using var client = await stack.GraphClientAsync("alice", Ct);

        await GetJsonAsync(client, "/v1.0/users/nobody-" + Guid.NewGuid().ToString("N") + "%40example.com", HttpStatusCode.NotFound);
        await GetJsonAsync(client, "/v1.0/users/" + Guid.NewGuid(), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_letter_avatar_is_no_photo()
    {
        await using var stack = LiveStack.Start();
        using var client = await stack.GraphClientAsync("alice", Ct);

        // Neither test user uploaded a picture; Nextcloud's generated avatar must read as "no photo".
        using var response = await client.GetAsync("/v1.0/users/bob%40nightware.localhost/photo/$value", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_contact_created_over_carddav_is_listed_and_ranked_for_alice()
    {
        await using var stack = LiveStack.Start();
        var uid = "nw5a-" + Guid.NewGuid().ToString("N");
        var address = uid + "@example.com";
        var card = $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nFN:Live Test {uid}\r\nN:Test;Live;;;\r\nEMAIL;TYPE=INTERNET:{address}\r\nEND:VCARD\r\n";
        var href = $"{LiveStack.CloudBase}/remote.php/dav/addressbooks/users/alice/contacts/{uid}.vcf";
        var nextcloud = await stack.NextcloudTokenAsync("alice", Ct);

        using (var put = new HttpRequestMessage(HttpMethod.Put, href) { Content = new StringContent(card, Encoding.UTF8, "text/vcard") })
        {
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nextcloud);
            put.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var created = await stack.Backchannel.SendAsync(put, Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        try
        {
            using var client = await stack.GraphClientAsync("alice", Ct);

            var contacts = await WalkAsync(client, "/v1.0/me/contacts?$select=displayName,emailAddresses&$top=999");
            var contact = contacts.Where(c => c["displayName"]!.GetValue<string>() == $"Live Test {uid}").ShouldHaveSingleItem();
            contact["emailAddresses"]![0]!["address"]!.GetValue<string>().ShouldBe(address);
            contacts.ShouldAllBe(static c => c["id"] != null);

            var people = await WalkAsync(client, "/v1.0/me/people?$select=displayName,scoredEmailAddresses&$top=999");
            var addresses = people.SelectMany(static p => p["scoredEmailAddresses"]!.AsArray().Select(static e => e!["address"]!.GetValue<string>())).ToList();
            addresses.ShouldContain(address, "contacts feed people");
            addresses.ShouldContain(Bob, "the directory feeds people");
            // No ordering against Bob: on a shared stack alice may have real mail with him, and recent
            // correspondence rightly outranks a saved contact nobody has written to. Exact ordering is
            // covered by the PeopleRanker unit tests.
            addresses.Count(a => a == address).ShouldBe(1, "people are merged by address, never duplicated");
        }
        finally
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, href);
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nextcloud);
            using var _ = await stack.Backchannel.SendAsync(delete, Ct);
        }
    }
}
