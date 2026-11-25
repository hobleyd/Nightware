using Microsoft.AspNetCore.Http;

using Nightware.Api.Features.Chats;

namespace Nightware.Api.Tests.Features.Chats;

public sealed class ChatKeySegmentMiddlewareTests
{
    [Theory]
    [InlineData("/v1.0/chats('19abc')/messages('61')", "/v1.0/chats/19abc/messages/61")]
    [InlineData("/v1.0/chats('19abc')", "/v1.0/chats/19abc")]
    [InlineData("/v1.0/chats('19abc')/members('MCMj')", "/v1.0/chats/19abc/members/MCMj")]
    [InlineData("/v1.0/teams('T1')/channels('c1')/messages('209')/replies('212')", "/v1.0/teams/T1/channels/c1/messages/209/replies/212")]
    [InlineData("/beta/chats('19abc')/messages", "/beta/chats/19abc/messages")]
    [InlineData("/v1.0/teams('T1')/operations('create.T1')", "/v1.0/teams/T1/operations/create.T1")]
    [InlineData("/v1.0/me/chats('19abc')/messages('61')", "/v1.0/me/chats/19abc/messages/61")]
    [InlineData("/v1.0/Chats('19abc')/Messages('61')", "/v1.0/Chats/19abc/Messages/61")]
    public void Keyed_chat_and_channel_paths_become_segments(string path, string expected)
    {
        ChatKeySegmentMiddleware.TryRewrite(new PathString(path), out var rewritten).ShouldBeTrue();
        rewritten.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("/v1.0/chats/19abc/messages/61")]
    [InlineData("/v1.0/me/mailFolders('Inbox')/messages")]
    [InlineData("/v1.0/me/messages('AAMk=')/attachments")]
    [InlineData("/v1.0/groups/g1/members('u1')")]
    [InlineData("/v1.0/me/drive/root/delta()")]
    [InlineData("/healthz")]
    public void Other_paths_pass_untouched(string path)
    {
        ChatKeySegmentMiddleware.TryRewrite(new PathString(path), out var rewritten).ShouldBeFalse();
        rewritten.Value.ShouldBe(path);
    }

    [Fact]
    public async Task The_request_is_routed_by_its_segment_form()
    {
        var seen = string.Empty;
        var middleware = new ChatKeySegmentMiddleware(context =>
        {
            seen = context.Request.Path.Value!;
            return Task.CompletedTask;
        });
        var http = new DefaultHttpContext();
        http.Request.Path = "/v1.0/chats('19abc')/messages('61')";

        await middleware.InvokeAsync(http);

        seen.ShouldBe("/v1.0/chats/19abc/messages/61");
    }
}
