using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Jmap.Core.Auth;

/// <summary>
/// Turns a <see cref="BackendCredential"/> into the <c>Authorization</c> header Stalwart expects.
/// <list type="bullet">
/// <item><c>Bearer</c>: the token as-is. A Bearer credential with <see cref="BackendCredential.ActAs"/> is refused earlier, by <c>JmapConnectionProvider</c>.</item>
/// <item><c>Basic</c>: <see cref="BackendCredential.Parameter"/> is either the raw <c>user:password</c>
/// (contains a <c>:</c>) or its base64 form (never contains <c>:</c>), so the two cannot be confused.</item>
/// <item><c>Basic</c> with <see cref="BackendCredential.ActAs"/>: Stalwart master-user impersonation,
/// login <c>&lt;ActAs&gt;%&lt;user&gt;</c> with the master user's password.</item>
/// </list>
/// </summary>
internal static class JmapAuthorization
{
    public static AuthenticationHeaderValue CreateHeader(BackendCredential credential, string masterUserSeparator)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrEmpty(masterUserSeparator);

        if (string.Equals(credential.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return new AuthenticationHeaderValue("Bearer", credential.Parameter);
        }

        if (string.Equals(credential.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            var (user, password) = SplitBasic(credential.Parameter);
            if (!string.IsNullOrWhiteSpace(credential.ActAs))
            {
                user = $"{credential.ActAs}{masterUserSeparator}{user}";
            }

            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        }

        return new AuthenticationHeaderValue(credential.Scheme, credential.Parameter);
    }

    /// <summary>A stable, non-reversible key identifying who a credential authenticates as (for the session cache).</summary>
    public static string IdentityKey(BackendCredential credential, string masterUserSeparator)
    {
        var header = CreateHeader(credential, masterUserSeparator);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{header.Scheme} {header.Parameter}"));
        return Convert.ToHexString(bytes);
    }

    private static (string User, string Password) SplitBasic(string parameter)
    {
        var raw = parameter.Contains(':', StringComparison.Ordinal) ? parameter : DecodeBase64(parameter);
        var colon = raw.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            throw new InvalidOperationException("A Basic backend credential must be 'user:password' or its base64 form.");
        }

        return (raw[..colon], raw[(colon + 1)..]);
    }

    private static string DecodeBase64(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("A Basic backend credential must be 'user:password' or its base64 form.", ex);
        }
    }
}
