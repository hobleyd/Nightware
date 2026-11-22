using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Application.Mail;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Routing;

/// <summary>
/// Phase 19: a list's <c>/$count</c> segment runs the list's own handler with <c>$count=true</c> and answers only the
/// total, as <c>text/plain</c>. The literal segment wins over the item route beside it (<c>messages/{id}</c>).
/// </summary>
public sealed class CountSegmentTests : IAsyncLifetime
{
    private readonly IMailMessageReader _messages = Substitute.For<IMailMessageReader>();
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services.AddSingleton(_messages).AddSingleton(_contacts));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private void MessagesTotal(int total) =>
        _messages.ListAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef?>(), Arg.Is<ItemQuery>(q => q.Count), Arg.Any<CancellationToken>())
            .Returns(new Page<MailMessage>([new MailMessage(new MessageId("e.m1"))]) { Total = total });

    [Theory]
    [InlineData("/v1.0/me/messages/$count", "me@example.com")]
    [InlineData("/v1.0/users/shared@example.com/messages/$count", "shared@example.com")]
    public async Task Messages_count_is_the_total_as_plain_text(string path, string mailbox)
    {
        MessagesTotal(41);

        var response = await _host.AuthorizedClient().GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("41");
        await _messages.Received(1).ListAsync(new MailboxRef(mailbox), null, Arg.Is<ItemQuery>(q => q.Count), Arg.Any<CancellationToken>());
        await _messages.DidNotReceiveWithAnyArgs().GetAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task The_filter_decides_what_is_counted()
    {
        MessagesTotal(3);

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/mailFolders/inbox/messages/$count?$filter=isRead eq false", Ct);

        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("3");
        await _messages.Received(1).ListAsync(
            Arg.Any<MailboxRef>(), new FolderRef.WellKnown(WellKnownFolder.Inbox), Arg.Is<ItemQuery>(q => q.Count && q.Filter != null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_list_counts_zero()
    {
        _contacts.ListAsync(Arg.Any<MailboxRef>(), Arg.Is<ItemQuery>(q => q.Count), Arg.Any<CancellationToken>())
            .Returns(new Page<Contact>([]) { Total = 0 });

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/contacts/$count", Ct);

        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("0");
    }

    [Fact]
    public async Task A_backend_error_is_the_graph_error()
    {
        _contacts.ListAsync(default, null!, Ct).ReturnsForAnyArgs(Result<Page<Contact>>.Failure(Error.NotFound("gone")));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/contacts/$count", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("ErrorItemNotFound");
    }

    [Fact]
    public async Task A_bad_query_option_is_400_before_the_handler()
    {
        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/messages/$count?$filter=nonsense(", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _messages.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_list_without_count_has_no_segment()
    {
        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/mailFolders/$count", Ct);

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }
}
