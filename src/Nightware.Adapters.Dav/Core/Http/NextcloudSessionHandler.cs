using System.Net;

namespace Nightware.Adapters.Dav.Core.Http;

/// <summary>
/// Sends the caller's own Nextcloud session cookies (<see cref="NextcloudSessions"/>) with each request and keeps the
/// ones Nextcloud sets, so a bearer request does not sign the user in again and mint a session token every time.
/// </summary>
/// <remarks>
/// Sits inside <see cref="DavAuthenticationHandler"/>, so it sees the <c>Authorization</c> header of each attempt;
/// the bearer token is always sent too, so an expired session falls back to a fresh sign-in. With a session cookie
/// present Nextcloud applies its CSRF check, which <c>OCS-APIRequest: true</c> satisfies. A 401 forgets the session.
/// The primary handler stays cookie-free: the <c>Cookie</c> header is written per request, from the caller's entry.
/// </remarks>
public sealed class NextcloudSessionHandler(NextcloudSessions sessions) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A Talk call keeps its own session (ADR 0009 D7): never mix it with the user's shared one.
        if (request.Options.TryGetValue(DavRequestOptions.OwnSession, out var own) && own)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        request.Headers.Remove("Cookie");
        var key = request.RequestUri is { IsAbsoluteUri: true } uri ? NextcloudSessions.KeyFor(uri, request.Headers.Authorization) : null;
        if (key is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (sessions.CookieHeader(key) is { } cookies)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
            if (!request.Headers.Contains("OCS-APIRequest"))
            {
                request.Headers.Add("OCS-APIRequest", "true");
            }
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            sessions.Forget(key);
        }
        else
        {
            sessions.Remember(key, response);
        }

        return response;
    }
}
