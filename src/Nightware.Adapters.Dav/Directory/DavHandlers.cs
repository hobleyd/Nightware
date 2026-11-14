namespace Nightware.Adapters.Dav.Directory;

/// <summary>Primary-handler adjustments for Nextcloud clients, applied to whatever handler the host built.</summary>
internal static class DavHandlers
{
    /// <summary>
    /// No cookie jar: Nextcloud's session cookie on a pooled handler would be replayed on other requests
    /// (and other users'), where Nextcloud rejects bearer + session with 401.
    /// </summary>
    public static void DisableCookies(HttpMessageHandler handler)
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

    /// <summary>Never follow redirects: Nextcloud redirects an unauthenticated request to its login page.</summary>
    public static void DisableRedirects(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.AllowAutoRedirect = false;
                break;
            case HttpClientHandler classic:
                classic.AllowAutoRedirect = false;
                break;
        }
    }
}
