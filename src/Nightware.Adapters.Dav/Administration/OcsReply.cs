using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Administration;

/// <summary>Which OCS endpoint answered; they differ in the success code and in what reaches the HTTP status.</summary>
internal enum OcsVersion
{
    /// <summary><c>/ocs/v1.php</c>: success is <c>statuscode</c> 100; HTTP is 200 for everything but 997 (401).</summary>
    V1,

    /// <summary><c>/ocs/v2.php</c>: success is <c>statuscode</c> 200; the HTTP status follows the OCS code (998 → 404, 997 → 401).</summary>
    V2,
}

/// <summary>
/// One OCS answer: <c>{"ocs":{"meta":{"status","statuscode","message"},"data":…}}</c>. A body that is not such an
/// envelope (a proxy's HTML page, an empty 405) is kept as <see cref="IsEnvelope"/> = false with the HTTP status
/// only — never mistaken for an OCS "not found".
/// </summary>
internal sealed record OcsReply(OcsVersion Version, int HttpStatus, bool IsEnvelope, int StatusCode, string? Message, JsonNode? Data)
{
    /// <summary>Seconds from <c>Retry-After</c>, when the server sent one.</summary>
    public int? RetryAfterSeconds { get; init; }

    public bool Succeeded => IsEnvelope && HttpStatus is >= 200 and < 300 && StatusCode == (Version == OcsVersion.V1 ? 100 : 200);

    /// <summary>OCS "not found": 998 (unknown route, or a user the caller may not see) or 404 (<c>OCSNotFoundException</c>).</summary>
    public bool IsOcsNotFound => IsEnvelope && StatusCode is 998 or 404;

    public static OcsReply Parse(OcsVersion version, HttpStatusCode status, string? body, int? retryAfterSeconds = null)
    {
        var http = (int)status;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                if (JsonNode.Parse(body) is JsonObject root
                    && root["ocs"] is JsonObject ocs
                    && ocs["meta"] is JsonObject meta
                    && meta["statuscode"] is JsonValue code
                    && TryInt(code, out var statusCode))
                {
                    var message = meta["message"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : null;
                    return new OcsReply(version, http, true, statusCode, string.IsNullOrWhiteSpace(message) ? null : message.Trim(), ocs["data"]?.DeepClone())
                    {
                        RetryAfterSeconds = retryAfterSeconds,
                    };
                }
            }
            catch (JsonException)
            {
                // not an envelope; fall through
            }
        }

        return new OcsReply(version, http, false, http, null, null) { RetryAfterSeconds = retryAfterSeconds };
    }

    /// <summary>
    /// The failure every operation shares; callers handle the codes that mean something specific to them first.
    /// A rejection of the console's own credential is <see cref="ErrorKind.BackendUnavailable"/> (a deployment
    /// fault for the operator), never <see cref="ErrorKind.BackendAuthFailed"/>, which would ask the signed-in
    /// administrator to sign in again.
    /// </summary>
    public Error ToError(string notFoundMessage)
    {
        if (HttpStatus == 401 || (IsEnvelope && StatusCode == 997))
        {
            return Error.BackendUnavailable("Nextcloud rejected the admin console's own credential; check NextcloudProvisioning:Username and NextcloudProvisioning:Password.");
        }

        if (HttpStatus == 403 || (IsEnvelope && StatusCode == 403))
        {
            return Error.BackendUnavailable("Nextcloud refused the admin console's account; it must be a Nextcloud administrator signing in with its password (password confirmation)." + Detail);
        }

        if (HttpStatus == 429)
        {
            return DavErrors.Throttled("Nextcloud is throttling the admin console.", RetryAfterSeconds);
        }

        if (HttpStatus == 412)
        {
            return Error.BackendUnavailable("Nextcloud refused the request as a CSRF check failure (OCS-APIRequest header missing?).");
        }

        if (!IsEnvelope)
        {
            return HttpStatus >= 500
                ? Error.BackendUnavailable($"Nextcloud failed (HTTP {HttpStatus}).", RetryAfterSeconds)
                : Error.BackendUnavailable($"Nextcloud sent an unexpected HTTP {HttpStatus} answer that is not an OCS response; check NextcloudProvisioning:BaseUrl.");
        }

        if (IsOcsNotFound)
        {
            return Error.NotFound(notFoundMessage);
        }

        return StatusCode switch
        {
            409 => Error.Conflict("Nextcloud reported a conflict." + Detail),
            400 or (> 100 and < 200) => Error.Validation($"Nextcloud rejected the request (OCS {StatusCode})." + Detail),
            _ => Error.BackendUnavailable($"Nextcloud failed (OCS {StatusCode}, HTTP {HttpStatus})." + Detail, RetryAfterSeconds),
        };
    }

    private string Detail => Message is null ? string.Empty : " " + Message;

    private static bool TryInt(JsonValue value, out int result)
    {
        result = 0;
        return value.GetValueKind() switch
        {
            JsonValueKind.Number => value.TryGetValue(out result),
            JsonValueKind.String => int.TryParse(value.GetValue<string>(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result),
            _ => false,
        };
    }
}
