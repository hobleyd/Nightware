using Microsoft.AspNetCore.Http;

using Nightware.Api.Auth;

namespace Nightware.Api.Features.Tasks.Upload;

/// <summary>
/// Recovers the caller's access token from a To Do upload URL (ADR 0001 §39): only for a
/// <c>PUT</c> to an upload-URL path whose sealed <c>authtoken</c> opens for exactly that path.
/// </summary>
/// <remarks>
/// A body of the wrong length is not an authentication failure — NightMail treats 401 as "sign in
/// again" — so the length is left to the attachment store, which refuses it with 400/413.
/// </remarks>
internal sealed class UploadUrlCredentialSource(UploadUrlProtector protector) : IRequestCredentialSource
{
    public bool TryResolve(HttpRequest request, out string bearerToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bearerToken = string.Empty;
        if (!HttpMethods.IsPut(request.Method)
            || !UploadUrl.TryParse(request.Path, out var binding)
            || request.Query[UploadUrl.AuthTokenParameter] is not [{ } sealedCredential]
            || !protector.TryOpen(sealedCredential, binding, out var ticket))
        {
            return false;
        }

        bearerToken = ticket.BearerToken;
        return true;
    }
}
