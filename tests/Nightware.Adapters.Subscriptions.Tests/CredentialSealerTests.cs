using Nightware.Adapters.Subscriptions;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Subscriptions.Tests;

public sealed class CredentialSealerTests
{
    private static readonly string Key = new('a', 64);

    [Fact]
    public void A_sealed_token_opens_for_its_subscription_only()
    {
        var sealer = new CredentialSealer(Key);

        var sealedValue = sealer.Seal("sub-1", "refresh-token");

        System.Text.Encoding.UTF8.GetString(sealedValue).ShouldNotContain("refresh-token");
        sealer.Open("sub-1", sealedValue).Value.ShouldBe("refresh-token");
        sealer.Open("sub-2", sealedValue).Error!.Kind.ShouldBe(ErrorKind.Unauthorized);
    }

    [Fact]
    public void Each_seal_is_different_and_tampering_or_another_key_fails()
    {
        var sealer = new CredentialSealer(Key);
        var one = sealer.Seal("s", "t");
        sealer.Seal("s", "t").ShouldNotBe(one);

        one[^1] ^= 1;
        sealer.Open("s", one).Error!.Kind.ShouldBe(ErrorKind.Unauthorized);
        new CredentialSealer(new string('b', 64)).Open("s", sealer.Seal("s", "t")).Error!.Kind.ShouldBe(ErrorKind.Unauthorized);
        sealer.Open("s", [1, 2, 3]).Error!.Kind.ShouldBe(ErrorKind.Unauthorized);
    }

    [Fact]
    public void A_short_key_is_refused() => Should.Throw<ArgumentException>(() => new CredentialSealer("abcd"));
}
