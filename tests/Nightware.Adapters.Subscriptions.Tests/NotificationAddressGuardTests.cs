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
    [InlineData("::")]
    [InlineData("255.255.255.255")]
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    [InlineData("240.0.0.1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    public void Private_and_special_addresses_are_refused(string address) =>
        NotificationAddressGuard.IsPublic(IPAddress.Parse(address)).ShouldBeFalse();

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("::ffff:1.1.1.1")]
    public void Public_addresses_are_allowed(string address) =>
        NotificationAddressGuard.IsPublic(IPAddress.Parse(address)).ShouldBeTrue();

    [Fact]
    public void A_refusal_is_recognised_however_deeply_it_is_wrapped()
    {
        NotificationAddressGuard.IsRefusal(new HttpRequestException("connect", new IOException("io", new NotificationAddressRefusedException()))).ShouldBeTrue();
        NotificationAddressGuard.IsRefusal(new HttpRequestException("connect", new IOException("io"))).ShouldBeFalse();
        NotificationAddressGuard.IsRefusal(null).ShouldBeFalse();
    }
}
