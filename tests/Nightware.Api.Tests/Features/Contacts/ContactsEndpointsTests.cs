using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Contacts;

public sealed class ContactsEndpointsTests : IAsyncLifetime
{
    private readonly IContactStore _store = Substitute.For<IContactStore>();
    private TestHost _host = null!;

    private static readonly Contact Pat = new(new ContactId("c-pat"), "Pat Contact")
    {
        GivenName = "Pat",
        Surname = "Contact",
        EmailAddresses = [new EmailAddress("pat@example.com", "Pat Contact"), new EmailAddress("pat.home@example.net")],
        Birthday = new DateOnly(1990, 4, 1),
        HomeAddress = new PhysicalAddress("1 Example St", "Sydney", "NSW", "2000", "Australia"),
    };

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services.AddSingleton(_store));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/v1.0/me/contacts", "me@example.com")]
    [InlineData("/v1.0/users/shared@example.com/contacts", "shared@example.com")]
    [InlineData("/v1.0/users/shared%40example.com/contacts", "shared@example.com")]
    public async Task Lists_the_mailboxs_contacts_in_both_forms(string path, string mailbox)
    {
        _store.ListAsync(new MailboxRef(mailbox), Arg.Is<ItemQuery>(q => q.Top == 999), Arg.Any<CancellationToken>())
            .Returns(new Page<Contact>([Pat], "next-page"));

        var response = await _host.AuthorizedClient().GetAsync(path + "?$select=displayName,emailAddresses&$top=999", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        var contact = body["value"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        contact.Select(static p => p.Key).ShouldBe(["id", "displayName", "emailAddresses"], ignoreOrder: true);
        var emails = contact["emailAddresses"]!.AsArray();
        emails[0].Str("name").ShouldBe("Pat Contact");
        emails[1].Str("name").ShouldBe("Pat Contact", "an address without its own name is named after its contact");
        body["@odata.nextLink"]?.GetValue<string>()!.ShouldStartWith(TestHost.GraphBase + "/");
        body["@odata.nextLink"]?.GetValue<string>()!.ShouldEndWith("$skiptoken=next-page");
    }

    [Fact]
    public async Task Without_select_the_full_contact_is_returned()
    {
        _store.ListAsync(new MailboxRef(TestHost.UserEmail), Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<Contact>([Pat]));

        var body = await (await _host.AuthorizedClient().GetAsync("/v1.0/me/contacts", Ct)).ReadObjectAsync();

        var contact = body["value"]![0]!;
        contact.Str("givenName").ShouldBe("Pat");
        contact.Str("birthday").ShouldBe("1990-04-01T11:59:00Z", "Exchange keeps birthdays at 11:59 UTC");
        contact.Str("homeAddress.city").ShouldBe("Sydney");
        body.ContainsKey("@odata.nextLink").ShouldBeFalse();
    }

    [Theory]
    [InlineData(ErrorKind.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(ErrorKind.Validation, HttpStatusCode.BadRequest)]
    [InlineData(ErrorKind.BackendUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Store_failures_become_graph_errors(ErrorKind kind, HttpStatusCode status)
    {
        _store.ListAsync(Arg.Any<MailboxRef>(), Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Error(kind, "c", "m"));

        (await _host.AuthorizedClient().GetAsync("/v1.0/me/contacts", Ct)).StatusCode.ShouldBe(status);
    }

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private static readonly MailboxRef Me = new(TestHost.UserEmail);

    [Fact]
    public async Task Creating_maps_the_body_and_answers_201_with_a_location()
    {
        _store.CreateAsync(Me, null, Arg.Any<ContactDraft>(), Arg.Any<CancellationToken>()).Returns(Pat);
        using var content = Json("""{"givenName":"Pat","birthday":"1990-04-01T00:00:00Z","emailAddresses":[{"name":"P","address":"pat@example.com"}],"id":"ignored","homeAddress":{"city":"Sydney"}}""");

        using var response = await _host.AuthorizedClient().PostAsync("/v1.0/me/contacts", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldEndWith("/contacts('c-pat')");
        await _store.Received().CreateAsync(Me, null, Arg.Is<ContactDraft>(d => d.GivenName == "Pat" && d.Birthday == new DateOnly(1990, 4, 1)
            && d.EmailAddresses!.Single() == new EmailAddress("pat@example.com", "P") && d.HomeAddress!.City == "Sydney" && d.Surname == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_null_in_a_patch_clears_and_an_absent_member_is_left_alone()
    {
        _store.UpdateAsync(Me, new ContactId("c-pat"), Arg.Any<ContactDraft>(), Arg.Any<CancellationToken>()).Returns(Pat);
        using var content = Json("""{"companyName":null,"businessPhones":null,"homeAddress":null,"birthday":null,"displayName":null}""");

        (await _host.AuthorizedClient().PatchAsync("/v1.0/me/contacts/c-pat", content, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await _store.Received().UpdateAsync(Me, new ContactId("c-pat"), Arg.Is<ContactDraft>(d => d.CompanyName == string.Empty && d.BusinessPhones!.Count == 0
            && d.HomeAddress!.IsEmpty && d.ClearBirthday && d.DisplayName == null && d.GivenName == null), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"extensions":[]}""")]
    [InlineData("""{"givenName":42}""")]
    [InlineData("""{"birthday":"not a date"}""")]
    public async Task Unsupported_or_malformed_members_are_400(string json)
    {
        using var content = Json(json);

        (await _host.AuthorizedClient().PostAsync("/v1.0/me/contacts", content, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task Get_and_delete_one_contact()
    {
        _store.GetAsync(Me, new ContactId("c-pat"), Arg.Any<CancellationToken>()).Returns(Pat);
        _store.DeleteAsync(Me, new ContactId("c-pat"), Arg.Any<CancellationToken>()).Returns(Result.Success());

        (await (await _host.AuthorizedClient().GetAsync("/v1.0/me/contacts/c-pat", Ct)).ReadObjectAsync()).Str("displayName").ShouldBe("Pat Contact");
        (await _host.AuthorizedClient().DeleteAsync("/v1.0/me/contacts/c-pat", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_photo_is_put_as_raw_bytes_and_streamed_back()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xD9];
        _store.SetPhotoAsync(Me, new ContactId("c-pat"), Arg.Any<byte[]>(), "image/jpeg", Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.GetPhotoAsync(Me, new ContactId("c-pat"), Arg.Any<CancellationToken>())
            .Returns(new BlobContent("image/jpeg", jpeg.Length, _ => Task.FromResult<Stream>(new MemoryStream(jpeg))));
        using var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

        using var put = await _host.AuthorizedClient().PutAsync("/v1.0/me/contacts/c-pat/photo/$value", content, Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await put.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        await _store.Received().SetPhotoAsync(Me, new ContactId("c-pat"), Arg.Is<byte[]>(b => b.SequenceEqual(jpeg)), "image/jpeg", Arg.Any<CancellationToken>());

        using var get = await _host.AuthorizedClient().GetAsync("/v1.0/me/contacts/c-pat/photo/$value", Ct);
        get.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        (await get.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(jpeg);
    }
}
