using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Application.Directory;
using Nightware.Application.Mail;
using Nightware.Domain.Contacts;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.People;

public sealed class PeopleEndpointsTests : IAsyncLifetime
{
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();
    private readonly IDirectoryAddresses _addresses = Substitute.For<IDirectoryAddresses>();
    private readonly IRecentCorrespondents _recent = Substitute.For<IRecentCorrespondents>();
    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _contacts.ListAsync(Arg.Any<MailboxRef>(), Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Page<Contact>.Empty);
        _directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>(
        [
            new DirectoryUser("u1", "Ann", "ann@example.com") { Mail = "ann@example.com" },
            new DirectoryUser("u2", "Bea", "bea@example.com") { Mail = "bea@example.com" },
        ]));
        _addresses.ListAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DirectoryAddress>>.Success([]));
        _recent.ListAsync(Arg.Any<MailboxRef>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Error.Unsupported("not yet"));
        _host = await TestHost.StartAsync(services => services.AddSingleton(_contacts).AddSingleton(_directory).AddSingleton(_addresses).AddSingleton(_recent));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/v1.0/me/people", "me@example.com")]
    [InlineData("/v1.0/users/shared@example.com/people", "shared@example.com")]
    public async Task Lists_ranked_people_in_both_forms(string path, string mailbox)
    {
        var response = await _host.AuthorizedClient().GetAsync(path + "?$select=displayName,scoredEmailAddresses&$top=999", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        var people = body["value"]!.AsArray();
        people.Select(static p => p.Str("displayName")).ShouldBe(["Ann", "Bea"]);
        people[0]!.AsObject().Select(static p => p.Key).ShouldBe(["id", "displayName", "scoredEmailAddresses"], ignoreOrder: true);
        people[0]!["scoredEmailAddresses"]![0].Str("address").ShouldBe("ann@example.com");
        people[0]!["scoredEmailAddresses"]![0].Str("selectionLikelihood").ShouldBe("notSpecified");
        await _contacts.Received().ListAsync(new MailboxRef(mailbox), Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pages_follow_the_next_link()
    {
        var client = _host.AuthorizedClient();
        var first = await (await client.GetAsync("/v1.0/me/people?$top=1", Ct)).ReadObjectAsync();
        var next = first["@odata.nextLink"]?.GetValue<string>()!;
        next.ShouldStartWith(TestHost.GraphBase + "/me/people?");

        var second = await (await client.GetAsync(next.Replace(TestHost.GraphBase, "/v1.0", StringComparison.Ordinal), Ct)).ReadObjectAsync();

        second["value"]!.AsArray().Select(static p => p.Str("displayName")).ShouldBe(["Bea"]);
        second.ContainsKey("@odata.nextLink").ShouldBeFalse();
    }

    [Theory]
    [InlineData("?$search=\"ann\"")]
    [InlineData("?$skiptoken=tampered")]
    public async Task Unsupported_or_tampered_queries_are_a_400(string query)
    {
        (await _host.AuthorizedClient().GetAsync("/v1.0/me/people" + query, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Groups_and_shared_mailboxes_are_suggested_beside_people()
    {
        _addresses.ListAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DirectoryAddress>>.Success(
            [new DirectoryAddress("household@example.com", "Household"), new DirectoryAddress("support@example.com", "Support Inbox")]));

        var body = await (await _host.AuthorizedClient().GetAsync("/v1.0/me/people?$select=displayName,scoredEmailAddresses&$top=999", Ct)).ReadObjectAsync();

        body["value"]!.AsArray().Select(static p => p.Str("displayName")).ShouldBe(["Ann", "Bea", "Household", "Support Inbox"]);
        body["value"]![3]!["scoredEmailAddresses"]![0].Str("address").ShouldBe("support@example.com");
    }
}
