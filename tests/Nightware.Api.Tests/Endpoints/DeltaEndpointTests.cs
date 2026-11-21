using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Api.Tests.Support;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Endpoints;

/// <summary>
/// The delta link envelope end to end, the way a phase wires <c>messages/delta</c> (mapped under
/// <c>/testFolders</c> so it does not collide with the real Phase 2 route).
/// </summary>
public sealed class DeltaEndpointTests : IAsyncLifetime
{
    private const string Select = "id,subject,isRead,receivedDateTime";
    private readonly IMailDeltaReader _delta = Substitute.For<IMailDeltaReader>();
    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync(
            services => services.AddSingleton(_delta),
            app =>
            {
                var graph = app.MapGroup(GraphPaths.V1).RequireAuthorization().AddEndpointFilter<GraphBindingFilter>();
                foreach (var scope in graph.MapMailboxGroups())
                {
                    scope.MapGet("/testFolders/{folderId}/messages/delta", static async (
                        Mailbox mailbox, string folderId, ODataQuery odata, HttpRequest request, DeltaLinkCodec links, IMailDeltaReader reader, CancellationToken ct) =>
                    {
                        var cursor = links.ReadCursor(request.Query, odata.Query);
                        if (!cursor.IsSuccess)
                        {
                            return GraphResults.Error(cursor.Error);
                        }

                        var c = cursor.Value;
                        var page = await reader.DeltaAsync(mailbox.Ref, new FolderRef.ById(new FolderId(folderId)), c.Token,
                            new DeltaRequest(c.Top ?? DeltaRequest.DefaultPageSize, c.Select), ct);
                        return GraphResults.Match(page, p => GraphResults.Delta(p, m => MessageDtoMapper.ToDto(m), c, "message", "#microsoft.graph.message"));
                    });
                }
            });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MailMessage Message(string id) => new(new MessageId(id))
    {
        Subject = "s",
        IsRead = true,
        ReceivedDateTime = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
        BodyPreview = "not selected",
    };

    [Fact]
    public async Task Initial_page_then_next_link_then_delta_link_keep_the_frozen_select_and_top()
    {
        _delta.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef>(), null, Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.More([new DeltaEntry<MailMessage>.Upsert(Message("e.1"))], new DeltaToken("page-2")));
        _delta.DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef>(), new DeltaToken("page-2"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done(
                [new DeltaEntry<MailMessage>.Removed("e.2", RemovedReason.Deleted), new DeltaEntry<MailMessage>.Removed("e.3", RemovedReason.Changed)],
                new DeltaToken("state-9")));
        var client = _host.AuthorizedClient();

        var first = await (await client.GetAsync($"/v1.0/me/testFolders/inbox/messages/delta?%24select={Uri.EscapeDataString(Select)}&%24top=50", Ct)).ReadObjectAsync();

        first["@odata.context"]!.GetValue<string>().ShouldBe("https://graph.test.example/v1.0/$metadata#Collection(message)");
        first["value"]![0]!.AsObject().Select(p => p.Key).ShouldBe(["id", "receivedDateTime", "subject", "isRead"]);
        var nextLink = first["@odata.nextLink"]!.GetValue<string>();
        nextLink.ShouldStartWith("https://graph.test.example/v1.0/me/testFolders/inbox/messages/delta?$skiptoken=");
        first.ContainsKey("@odata.deltaLink").ShouldBeFalse();

        // Replayed verbatim: no $select/$top on the wire, they come out of the link.
        var second = await (await client.GetAsync(nextLink, Ct)).ReadObjectAsync();

        await _delta.Received().DeltaAsync(new MailboxRef("me@example.com"), new FolderRef.ById(new FolderId("inbox")), new DeltaToken("page-2"),
            Arg.Is<DeltaRequest>(r => r.PageSize == 50 && r.Select!.SetEquals(Select.Split(',', StringSplitOptions.None))), Arg.Any<CancellationToken>());
        second["value"]![0]!.ToJsonString().ShouldBe("""{"@odata.type":"#microsoft.graph.message","id":"e.2","@removed":{"reason":"deleted"}}""");
        second["value"]![1]!["@removed"]!["reason"]!.GetValue<string>().ShouldBe("changed");
        second["@odata.deltaLink"]!.GetValue<string>().ShouldStartWith("https://graph.test.example/v1.0/me/testFolders/inbox/messages/delta?$deltatoken=");
    }

    [Fact]
    public async Task A_delta_link_is_accepted_as_a_skiptoken_and_vice_versa()
    {
        _delta.DeltaAsync(default, default!, default, default!, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(DeltaPage<MailMessage>.Done([], new DeltaToken("state-1")));
        var client = _host.AuthorizedClient();
        var deltaLink = (await (await client.GetAsync("/v1.0/me/testFolders/inbox/messages/delta", Ct)).ReadObjectAsync())["@odata.deltaLink"]!.GetValue<string>();
        var token = deltaLink[(deltaLink.IndexOf('=', StringComparison.Ordinal) + 1)..];

        var response = await client.GetAsync($"/v1.0/me/testFolders/inbox/messages/delta?$skiptoken={token}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _delta.Received().DeltaAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef>(), new DeltaToken("state-1"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_link_that_fails_its_signature_is_a_410_so_the_client_resyncs()
    {
        var codec = _host.App.Services.GetRequiredService<DeltaLinkCodec>();
        var link = codec.Encode(new DeltaLinkState(new DeltaToken("t"), null, 50));
        var tampered = "eyJ2IjoxLCJ0IjoieCIsImkiOjB9" + link[link.IndexOf('.', StringComparison.Ordinal)..];

        var response = await _host.AuthorizedClient().GetAsync($"/v1.0/me/testFolders/inbox/messages/delta?$deltatoken={tampered}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("syncStateNotFound");
    }

    [Fact]
    public async Task An_undecodable_link_is_a_410_sync_state_not_found()
    {
        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/testFolders/inbox/messages/delta?$deltatoken=LztZwWjo5IivWBhyxw5rACKxf7mPm0oW6JZZ7fvKxYPS_67JnEYmfQQMPccy6FRun0DWJF5775dvuXxlZnMYhBubC1v4SBVT9ZjO8f7acZI", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("syncStateNotFound");
    }

    [Fact]
    public async Task Adapter_sync_state_loss_is_a_410()
    {
        _delta.DeltaAsync(default, default!, default, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Error.SyncStateGone("cannotCalculateChanges"));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/testFolders/inbox/messages/delta", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }
}
