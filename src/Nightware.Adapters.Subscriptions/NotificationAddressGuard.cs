using System.Net;
using System.Net.Sockets;

namespace Nightware.Adapters.Subscriptions;

/// <summary>
/// The SSRF guard (ADR 0006 D10): a notification is only ever sent to a public address. The check runs in the
/// socket's connect callback, after resolution and immediately before connecting to the checked address, so a
/// name that re-resolves to a private address between a check and the send (DNS rebinding) gains nothing.
/// </summary>
internal static class NotificationAddressGuard
{
    /// <summary>Whether <paramref name="exception"/> (or one it wraps: HttpClient wraps connect failures) is the guard's refusal.</summary>
    public static bool IsRefusal(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is NotificationAddressRefusedException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="address"/> is a public unicast address.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                   // "this" network
                || b[0] == 10                                    // private
                || (b[0] == 100 && (b[1] & 0xC0) == 64)          // carrier-grade NAT 100.64/10
                || (b[0] == 169 && b[1] == 254)                  // link-local
                || (b[0] == 172 && (b[1] & 0xF0) == 16)          // private 172.16/12 (Docker's default bridges)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // IETF protocol assignments
                || (b[0] == 192 && b[1] == 168)                  // private
                || (b[0] == 198 && (b[1] & 0xFE) == 18)          // benchmarking 198.18/15
                || b[0] >= 224);                                 // multicast and reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC                         // unique local fc00::/7
                || address.IsIPv6Teredo);
        }

        return false;
    }

    /// <summary>
    /// The connect callback: resolves the host, keeps only public addresses (or any address for a test-only
    /// origin), and connects to the first that answers.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, IReadOnlyCollection<string> testOnlyOrigins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var exempt = context.InitialRequestMessage.RequestUri is { } uri && testOnlyOrigins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var allowed = addresses.Where(a => exempt || IsPublic(a)).ToList();
        if (allowed.Count == 0)
        {
            throw new NotificationAddressRefusedException();
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed.ToArray(), context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>Raised by the connect callback when no resolved address may be sent to.</summary>
internal sealed class NotificationAddressRefusedException() : Exception("The notification URL resolves to an address Nightware does not send to.");
