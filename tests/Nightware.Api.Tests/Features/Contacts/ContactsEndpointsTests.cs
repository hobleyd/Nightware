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
        contact.Str("birthday").ShouldBe("1990-04-01T00:00:00Z");
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

    [Fact]
    public async Task Contact_writes_are_unknown_routes()
    {
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        (await _host.AuthorizedClient().PostAsync("/v1.0/me/contacts", content, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "contacts are read-only; unmapped Graph routes answer 400 (ADR 0001 §31)");
    }
}
