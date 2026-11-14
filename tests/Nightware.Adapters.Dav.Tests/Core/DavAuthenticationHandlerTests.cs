using System.Text;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Dav.Tests.Core;

public sealed class DavAuthenticationHandlerTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private static async Task<string?> SendWith(BackendCredential? credential, Capture? capture = null)
    {
        capture ??= new Capture();
        using var invoker = new HttpMessageInvoker(new DavAuthenticationHandler { InnerHandler = capture });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cloud.example.com/remote.php/dav/");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer stale");
        if (credential is not null)
        {
            request.Options.Set(DavRequestOptions.Credential, credential);
        }

        using var _ = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
        return capture.Authorizations[^1];
    }

    [Fact]
    public async Task Bearer_tokens_pass_through() =>
        (await SendWith(new BackendCredential("Bearer", "abc.def", CredentialStrategy.TokenExchange))).ShouldBe("Bearer abc.def");

    [Fact]
    public async Task Raw_basic_credentials_are_encoded() =>
        (await SendWith(new BackendCredential("Basic", "nightware:app-pass-word", CredentialStrategy.ServiceAccount)))
            .ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("nightware:app-pass-word")));

    [Fact]
    public async Task Pre_encoded_basic_credentials_pass_through()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("svc:pw"));
        (await SendWith(new BackendCredential("Basic", encoded, CredentialStrategy.ServiceAccount))).ShouldBe("Basic " + encoded);
    }

    [Fact]
    public async Task Requests_without_a_credential_are_anonymous() =>
        (await SendWith(null)).ShouldBeNull();

    [Fact]
    public async Task The_same_handler_serves_different_users()
    {
        var capture = new Capture();
        using var invoker = new HttpMessageInvoker(new DavAuthenticationHandler { InnerHandler = capture });
        foreach (var token in new[] { "alice-token", "bob-token" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://cloud.example.com/remote.php/dav/");
            request.Options.Set(DavRequestOptions.Credential, new BackendCredential("Bearer", token, CredentialStrategy.PassThrough));
            using var _ = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
        }

        capture.Authorizations.ShouldBe(["Bearer alice-token", "Bearer bob-token"]);
    }
}
