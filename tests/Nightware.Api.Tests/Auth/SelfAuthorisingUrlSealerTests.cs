using System.Buffers.Text;
using System.Text.Json.Serialization;

using Nightware.Api.Auth;

namespace Nightware.Api.Tests.Auth;

public sealed class SelfAuthorisingUrlSealerTests
{
    private static readonly string[] Binding = ["GET", "a", "b"];
    private static readonly SealerTestPayload Payload = new("secret-token", 42);

    private static SelfAuthorisingUrlSealer Sealer(string? key = "k", string purpose = "test/one") => new(key, purpose);

    [Fact]
    public void Round_trips_for_the_same_key_purpose_and_binding()
    {
        var sealedValue = Sealer().Seal(Payload, SealerTestJson.Default.SealerTestPayload, Binding);

        Sealer().TryOpen(sealedValue, SealerTestJson.Default.SealerTestPayload, Binding, out var opened).ShouldBeTrue();
        opened.ShouldBe(Payload);
        sealedValue.ShouldNotContain("secret");
        Base64Url.IsValid(sealedValue).ShouldBeTrue();
    }

    [Fact]
    public void Every_seal_uses_a_fresh_nonce() =>
        Sealer().Seal(Payload, SealerTestJson.Default.SealerTestPayload, Binding)
            .ShouldNotBe(Sealer().Seal(Payload, SealerTestJson.Default.SealerTestPayload, Binding));

    [Fact]
    public void Another_purpose_or_key_cannot_open_it()
    {
        var sealedValue = Sealer().Seal(Payload, SealerTestJson.Default.SealerTestPayload, Binding);

        Sealer(purpose: "test/two").TryOpen(sealedValue, SealerTestJson.Default.SealerTestPayload, Binding, out _).ShouldBeFalse();
        Sealer("other").TryOpen(sealedValue, SealerTestJson.Default.SealerTestPayload, Binding, out _).ShouldBeFalse();
    }

    public static TheoryData<string> OtherBindings() => ["GET|a|c", "GET|ab|", "GET|a", "GET|a|b|"];

    [Theory]
    [MemberData(nameof(OtherBindings))]
    public void Any_other_binding_cannot_open_it(string joined) =>
        Sealer().TryOpen(Sealer().Seal(Payload, SealerTestJson.Default.SealerTestPayload, Binding), SealerTestJson.Default.SealerTestPayload, joined.Split('|'), out _).ShouldBeFalse();

    [Fact]
    public void A_flipped_bit_cannot_open_it()
    {
        var bytes = Base64Url.DecodeFromChars(Sealer().Seal(Payload, SealerTestJson.Default.SealerTestPayload, Binding));
        bytes[^1] ^= 1;

        Sealer().TryOpen(Base64Url.EncodeToString(bytes), SealerTestJson.Default.SealerTestPayload, Binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void Another_version_byte_cannot_open_it()
    {
        var bytes = Base64Url.DecodeFromChars(Sealer().Seal(Payload, SealerTestJson.Default.SealerTestPayload, Binding));
        bytes[0] = 2;

        Sealer().TryOpen(Base64Url.EncodeToString(bytes), SealerTestJson.Default.SealerTestPayload, Binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void Oversized_values_are_refused_unread() =>
        Sealer().TryOpen(new string('A', SelfAuthorisingUrlSealer.MaxSealedLength + 1), SealerTestJson.Default.SealerTestPayload, Binding, out _).ShouldBeFalse();

    [Fact]
    public void A_sealed_non_object_does_not_open_as_a_payload()
    {
        var sealedValue = Sealer().Seal("just a string", SealerTestJson.Default.String, Binding);

        Sealer().TryOpen(sealedValue, SealerTestJson.Default.SealerTestPayload, Binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_purpose_is_required() =>
        Should.Throw<ArgumentException>(() => new SelfAuthorisingUrlSealer("k", " "));
}

public sealed record SealerTestPayload(string Token, long Number);

[JsonSerializable(typeof(SealerTestPayload))]
[JsonSerializable(typeof(string))]
internal sealed partial class SealerTestJson : JsonSerializerContext;
