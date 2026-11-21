using System.Net;
using Nightware.Adapters.Subscriptions;

namespace Nightware.Adapters.Subscriptions.Tests;

public sealed class NotificationAddressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.18.0.5")]
    [InlineData("192.168.18.7")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void Private_and_special_addresses_are_refused(string address) =>
        NotificationAddressGuard.IsPublic(IPAddress.Parse(address)).ShouldBeFalse();

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("172.32.0.1")]
    public void Public_addresses_are_allowed(string address) =>
        NotificationAddressGuard.IsPublic(IPAddress.Parse(address)).ShouldBeTrue();
}
