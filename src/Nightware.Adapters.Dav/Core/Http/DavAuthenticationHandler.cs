using System.Net.Http.Headers;
using System.Text;
using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Dav.Core.Http;

/// <summary>Per-request data the <see cref="DavClient"/> hands to its handler pipeline.</summary>
public static class DavRequestOptions
{
    /// <summary>The caller's credential, attached by <see cref="DavClient"/> (which lives in the request scope).</summary>
    public static readonly HttpRequestOptionsKey<BackendCredential> Credential = new("Nightware.Dav.Credential");
}

/// <summary>
/// Writes the <c>Authorization</c> header from the credential the client attached to the
/// request. It deliberately holds no scoped services: <c>IHttpClientFactory</c> builds and
/// pools handlers outside the request scope, so a handler that resolved
/// <see cref="IBackendCredentials"/> itself could send one user's token on another's request.
/// Because the header is written per attempt, retries re-apply it.
/// </summary>
public sealed class DavAuthenticationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options.TryGetValue(DavRequestOptions.Credential, out var credential))
        {
            request.Headers.Authorization = ToHeader(credential);
        }
        else
        {
            request.Headers.Authorization = null;
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// <c>Bearer</c> tokens pass through. A <c>Basic</c> parameter may be given raw
    /// (<c>user:app-password</c>) or already base64-encoded; raw values are encoded here.
    /// </summary>
    public static AuthenticationHeaderValue ToHeader(BackendCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.Equals(credential.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            && credential.Parameter.Contains(':', StringComparison.Ordinal))
        {
            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Parameter)));
        }

        return new AuthenticationHeaderValue(credential.Scheme, credential.Parameter);
    }
}
