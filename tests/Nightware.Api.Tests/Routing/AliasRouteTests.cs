using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Contacts;
using Nightware.Application.Mail;

namespace Nightware.Api.Tests.Routing;

/// <summary>
/// Phase 18's folder-scoped item routes (<c>…/messages/{id}</c>, <c>…/contacts/{id}</c>) sit beside the
/// <c>…/delta</c> literals; a delta request must still reach delta (a tampered link is its 410 syncStateNotFound), never the item read.
/// </summary>
public sealed class AliasRouteTests : IAsyncLifetime
{
    private readonly IMailMessageReader _messages = Substitute.For<IMailMessageReader>();
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services.AddSingleton(_messages).AddSingleton(_contacts));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=garbage")]
    [InlineData("/v1.0/me/contactFolders/f1/contacts/delta?$deltatoken=garbage")]
    public async Task A_delta_request_still_reaches_delta(string path)
    {
        var response = await _host.AuthorizedClient().GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone, await response.Content.ReadAsStringAsync(Ct));
        _messages.ReceivedCalls().ShouldBeEmpty();
        _contacts.ReceivedCalls().ShouldBeEmpty();
    }
}
