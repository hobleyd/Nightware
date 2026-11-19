using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nightware.Adapters.DnsHosts.Administration;

/// <summary>
/// AWS Signature Version 4 for one request: signs the method, path, query, <c>host</c> and <c>x-amz-date</c>
/// headers and the body, and sets <c>Authorization</c>. The secret never leaves this class.
/// </summary>
internal static class AwsSignature
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <param name="body">The request body exactly as sent (empty for none).</param>
    public static void Sign(HttpRequestMessage request, byte[] body, string accessKeyId, string secretKey, string region, string service, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        Uri uri = request.RequestUri ?? throw new ArgumentException("The request has no URI.", nameof(request));

        string amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = amzDate[..8];
        string host = uri.IsDefaultPort ? uri.Host : uri.Authority;
        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);

        const string signedHeaders = "host;x-amz-date";
        string canonicalRequest = string.Join(
            '\n',
            request.Method.Method,
            CanonicalPath(uri),
            CanonicalQuery(uri),
            $"host:{host}\nx-amz-date:{amzDate}\n",
            signedHeaders,
            Hex(SHA256.HashData(body)));

        string scope = $"{date}/{region}/{service}/aws4_request";
        string stringToSign = string.Join('\n', Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        byte[] key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        string signature = Hex(Hmac(key, stringToSign));

        request.Headers.TryAddWithoutValidation("Authorization", $"{Algorithm} Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>Each path segment URI-encoded once (RFC 3986 unreserved characters kept).</summary>
    private static string CanonicalPath(Uri uri)
    {
        string path = uri.AbsolutePath;
        return path.Length == 0 ? "/" : string.Join('/', path.Split('/').Select(static s => Encode(Uri.UnescapeDataString(s))));
    }

    /// <summary>Parameters sorted by name (then value), names and values URI-encoded.</summary>
    private static string CanonicalQuery(Uri uri)
    {
        string query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return string.Empty;
        }

        return string.Join('&', query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static p => p.Split('=', 2))
            .Select(static p => (Name: Encode(Uri.UnescapeDataString(p[0])), Value: Encode(Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty))))
            .OrderBy(static p => p.Name, StringComparer.Ordinal)
            .ThenBy(static p => p.Value, StringComparer.Ordinal)
            .Select(static p => $"{p.Name}={p.Value}"));
    }

    private static string Encode(string text)
    {
        var encoded = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
