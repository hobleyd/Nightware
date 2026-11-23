using System.Text;

using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Calls;

namespace Nightware.Adapters.Dav.Tests.Calls;

public sealed class CallIdSealerTests
{
    private static CallIdSealer Sealer(string key = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff") =>
        new(Options.Create(new CallOptions { SealingKey = key }));

    [Fact]
    public void A_sealed_id_opens_for_the_same_caller_only()
    {
        var sealer = Sealer();
        var id = sealer.Seal("alice", Encoding.UTF8.GetBytes("payload"));

        id.ShouldNotContain("payload");
        Encoding.UTF8.GetString(sealer.Open("alice", id)!).ShouldBe("payload");
        sealer.Open("bob", id).ShouldBeNull();
        Sealer("ff112233445566778899aabbccddeeff00112233445566778899aabbccddeeff").Open("alice", id).ShouldBeNull();
        sealer.Seal("alice", [1]).ShouldNotBe(sealer.Seal("alice", [1]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("AAAA")]
    public void Garbage_opens_to_nothing(string id) => Sealer().Open("alice", id).ShouldBeNull();

    [Fact]
    public void Tampering_opens_to_nothing()
    {
        var sealer = Sealer();
        var id = sealer.Seal("alice", [1, 2, 3]).ToCharArray();
        id[^1] = id[^1] == 'A' ? 'B' : 'A';

        sealer.Open("alice", new string(id)).ShouldBeNull();
    }

    [Fact]
    public void A_short_key_is_refused() =>
        Should.Throw<ArgumentException>(() => Sealer("0011"));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", true)]
    [InlineData("0011", false)]
    [InlineData("zz112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", false)]
    [InlineData("00112233445566778899aabbccddeeff00112233445566778899aabbccddeef", false)]
    public void Options_are_off_or_usable(string? key, bool valid) =>
        new CallOptionsValidator().Validate(null, new CallOptions { SealingKey = key }).Succeeded.ShouldBe(valid);
}
