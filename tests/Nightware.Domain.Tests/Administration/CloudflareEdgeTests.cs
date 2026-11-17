using System.Net;

using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class CloudflareEdgeTests
{
    [Theory]
    [InlineData("104.21.32.1", true)]
    [InlineData("172.67.140.10", true)]
    [InlineData("188.114.97.3", true)]
    [InlineData("2606:4700:3030::6815:2001", true)]
    [InlineData("::ffff:104.21.32.1", true)]
    [InlineData("81.2.69.160", false)]
    [InlineData("2a01:4f8:c0c:1::1", false)]
    [InlineData("1.1.1.1", false)]
    public void Knows_cloudflares_proxy_addresses_from_a_servers_own(string address, bool proxy)
    {
        CloudflareEdge.Contains(IPAddress.Parse(address)).ShouldBe(proxy);
    }
}
