using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Jmap.Core.Auth;

/// <summary>
/// Applies the request's <see cref="JmapRequestOptions.Credential"/> as the <c>Authorization</c>
/// header. A request with neither a credential nor the anonymous flag is a bug: Stalwart answers
/// an anonymous session fetch with 200 and no accounts, which would otherwise surface as "not found".
/// </summary>
internal sealed class JmapAuthenticationHandler(IOptions<JmapOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options.TryGetValue(JmapRequestOptions.Credential, out var credential))
        {
            request.Headers.Authorization = JmapAuthorization.CreateHeader(credential, options.Value.MasterUserSeparator);
        }
        else if (!request.Options.TryGetValue(JmapRequestOptions.Anonymous, out var anonymous) || !anonymous)
        {
            throw new InvalidOperationException("JMAP request sent without a backend credential.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
