using Microsoft.Extensions.Http;

namespace Nightware.Hosting.Http;

/// <summary>
/// Turns cookies off on the primary handler of <b>every</b> <see cref="IHttpClientFactory"/> client
/// in the façade, whoever registered it.
/// </summary>
/// <remarks>
/// <para>The factory pools primary handlers and shares them across requests, so a handler's
/// <c>CookieContainer</c> is shared across <i>users</i>. Nextcloud answers a bearer request with a
/// session cookie; with cookies on, the next user's request replayed it — CSRF 401s live, and a
/// path to another user's data. The adapters now build cookie-free handlers themselves; this filter
/// is the backstop so a client added later (or one that forgets) cannot regress it. The façade is
/// stateless towards its backends except for one thing: Nextcloud's session, kept per user by
/// <c>NextcloudSessionHandler</c>, which writes the <c>Cookie</c> header itself above the primary handler.</para>
/// <para>A primary handler of another type (a test stub) is left alone;
/// <c>HttpClientCookiePolicyTests</c> asserts the real composition has none.</para>
/// </remarks>
internal sealed class CookielessHttpClientsFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return builder =>
        {
            next(builder);
            DisableCookies(builder.PrimaryHandler);
        };
    }

    internal static void DisableCookies(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.UseCookies = false;
                break;
            case HttpClientHandler classic:
                classic.UseCookies = false;
                break;
        }
    }
}
