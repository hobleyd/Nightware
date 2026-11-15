using Microsoft.AspNetCore.Http;

using Nightware.Api.Auth;

namespace Nightware.Api.Features.Files.Download;

/// <summary>
/// Recovers the caller's access token from a share download URL (ADR 0001 §39): only for a <c>GET</c> of a
/// download-URL path whose sealed <c>authtoken</c> opens for exactly that share id and format.
/// </summary>
internal sealed class DownloadUrlCredentialSource(DownloadUrlProtector protector) : IRequestCredentialSource
{
    public bool TryResolve(HttpRequest request, out string bearerToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bearerToken = string.Empty;
        if (!HttpMethods.IsGet(request.Method)
            || !DownloadUrl.TryParse(request.Path, out var shareId)
            || request.Query[DownloadUrl.AuthTokenParameter] is not [{ } sealedCredential]
            || request.Query[DownloadUrl.FormatParameter].Count > 1
            || !DownloadUrl.TryParseFormat(request.Query[DownloadUrl.FormatParameter].ToString(), out var format)
            || !protector.TryOpen(sealedCredential, new DownloadUrlBinding(shareId, format), out var ticket))
        {
            return false;
        }

        bearerToken = ticket.BearerToken;
        return true;
    }
}
