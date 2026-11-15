using Microsoft.Extensions.Options;

using Nightware.Api.Auth;
using Nightware.Api.Features.Tasks.Upload;
using Nightware.Api.Graph.Json;
using Nightware.Api.Tests.Auth;

namespace Nightware.Api.Tests.Features.Tasks;

public sealed class UploadUrlTests
{
    [Fact]
    public void Builds_a_url_whose_route_values_bind_back()
    {
        var binding = new UploadUrlBinding("alice@example.com", "bGlzdA", "dGFzaw", "c2Vzc2lvbg");

        var url = new Uri(UploadUrl.Build("https://graph.example/v1.0", binding, "sealed-value"));

        url.AbsolutePath.ShouldBe("/v1.0/users/alice@example.com/todo/lists/bGlzdA/tasks/dGFzaw/attachmentSessions/c2Vzc2lvbg/content");
        url.Query.ShouldBe("?authtoken=sealed-value");
        var routed = SelfAuthorisingUrlCredentialSourceTests.Routed("PUT", UploadUrl.Kind, url.Query,
            ("user", "Alice@example.com"), ("listId", "bGlzdA"), ("taskId", "dGFzaw"), ("sessionId", "c2Vzc2lvbg"));
        UploadUrl.Kind.BindingOf(routed.Request).ShouldBe(binding.BoundTo);
    }

    [Fact]
    public void A_route_missing_a_value_binds_nothing()
    {
        var routed = SelfAuthorisingUrlCredentialSourceTests.Routed("PUT", UploadUrl.Kind, string.Empty, ("user", "a@b.c"), ("listId", "l"), ("taskId", ""), ("sessionId", "s"));

        UploadUrl.Kind.BindingOf(routed.Request).ShouldBeNull();
    }

    [Fact]
    public void An_upload_url_is_put_only_and_the_url_is_its_only_credential()
    {
        UploadUrl.Kind.Method.ShouldBe("PUT");
        UploadUrl.Kind.UrlOnly.ShouldBeTrue();
        UploadUrl.Kind.Purpose.ShouldBe("nightware/todo-upload-url/v1", "changing the purpose would break URLs already handed out");
    }

    [Fact]
    public void A_realistic_access_token_fits_the_request_line()
    {
        var sealer = new SelfAuthorisingUrlSealer(Options.Create(new DeltaLinkOptions { SigningKey = "k" }), TimeProvider.System);
        var ticket = new SealedTicket(new string('x', 2600), DateTimeOffset.UtcNow.AddMinutes(15)) { Name = new string('n', 255), Size = 4096 };
        var binding = new UploadUrlBinding("alice@nightware.localhost", new string('L', 90), new string('T', 140), new string('S', 420));

        var url = UploadUrl.Build("https://graph.nightware.localhost/v1.0", binding, sealer.Seal(UploadUrl.Kind, ticket, binding.BoundTo));

        // Leaves room for an access token twice this size before the request line overflows.
        url.Length.ShouldBeLessThan(SelfAuthorisingUrl.MaxRequestLine * 2 / 3);
    }
}
