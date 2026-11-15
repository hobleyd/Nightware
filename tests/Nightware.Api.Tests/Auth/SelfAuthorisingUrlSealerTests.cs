using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Nightware.Api.Auth;
using Nightware.Api.Graph.Json;

namespace Nightware.Api.Tests.Auth;

public sealed class SelfAuthorisingUrlSealerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly SelfAuthorisingUrl Kind = new("test/one", "PUT", TimeSpan.FromMinutes(15), static _ => null);
    private static readonly string[] Binding = ["alice@example.com", "list-1", "task-1", "session-1"];
    private static readonly SealedTicket Ticket = new("header.payload.signature", Now.AddMinutes(15)) { Name = "big.eml", Size = 4096 };

    private readonly FakeTimeProvider _clock = new(Now);

    private SelfAuthorisingUrlSealer Sealer(string? key = "shared-signing-key") =>
        new(Options.Create(new DeltaLinkOptions { SigningKey = key }), _clock);

    private static SelfAuthorisingUrl KindOf(string purpose = "test/one", string method = "PUT") => new(purpose, method, TimeSpan.FromMinutes(15), static _ => null);

    [Fact]
    public void A_sealed_ticket_opens_for_its_own_binding_on_any_instance()
    {
        var sealedValue = Sealer().Seal(Kind, Ticket, Binding);

        Sealer().TryOpen(Kind, sealedValue, Binding, out var ticket).ShouldBeTrue();
        ticket.ShouldBe(Ticket);
        sealedValue.ShouldNotContain("payload", Case.Insensitive);
        sealedValue.ShouldNotContain("=");
        sealedValue.ShouldNotContain(".");
        Base64Url.IsValid(sealedValue).ShouldBeTrue();
    }

    [Fact]
    public void A_ticket_without_a_file_round_trips_without_one()
    {
        var plain = new SealedTicket("t", Now.AddMinutes(5));

        Sealer().TryOpen(Kind, Sealer().Seal(Kind, plain, Binding), Binding, out var ticket).ShouldBeTrue();
        ticket.ShouldBe(plain);
    }

    [Fact]
    public void Every_seal_uses_a_fresh_nonce() =>
        Sealer().Seal(Kind, Ticket, Binding).ShouldNotBe(Sealer().Seal(Kind, Ticket, Binding));

    [Fact]
    public void Another_purpose_method_or_key_cannot_open_it()
    {
        var sealedValue = Sealer().Seal(Kind, Ticket, Binding);

        Sealer().TryOpen(KindOf(purpose: "test/two"), sealedValue, Binding, out _).ShouldBeFalse();
        Sealer().TryOpen(KindOf(method: "GET"), sealedValue, Binding, out _).ShouldBeFalse();
        Sealer("another-key").TryOpen(Kind, sealedValue, Binding, out _).ShouldBeFalse();
    }

    public static TheoryData<string> OtherBindings() =>
    [
        "bob@example.com|list-1|task-1|session-1",
        "alice@example.com|list-2|task-1|session-1",
        "alice@example.com|list-1|task-2|session-1",
        "alice@example.com|list-1|task-1|session-2",
        "alice@example.com|list-1task-1||session-1",
        "alice@example.com|list-1|task-1",
        "alice@example.com|list-1|task-1|session-1|",
    ];

    [Theory]
    [MemberData(nameof(OtherBindings))]
    public void Any_other_binding_cannot_open_it(string joined) =>
        Sealer().TryOpen(Kind, Sealer().Seal(Kind, Ticket, Binding), joined.Split('|'), out _).ShouldBeFalse();

    [Fact]
    public void Flipping_any_byte_breaks_it()
    {
        var bytes = Base64Url.DecodeFromChars(Sealer().Seal(Kind, Ticket, Binding));
        for (var i = 0; i < bytes.Length; i++)
        {
            var tampered = (byte[])bytes.Clone();
            tampered[i] ^= 0x01;

            Sealer().TryOpen(Kind, Base64Url.EncodeToString(tampered), Binding, out _).ShouldBeFalse($"byte {i}");
        }
    }

    [Fact]
    public void A_truncated_value_does_not_open()
    {
        var sealedValue = Sealer().Seal(Kind, Ticket, Binding);

        foreach (var length in new[] { 0, 1, 10, 28, 29, sealedValue.Length - 1 })
        {
            Sealer().TryOpen(Kind, sealedValue[..length], Binding, out _).ShouldBeFalse($"length {length}");
        }
    }

    [Fact]
    public void It_expires()
    {
        var sealedValue = Sealer().Seal(Kind, Ticket, Binding);
        _clock.Advance(TimeSpan.FromMinutes(15));

        Sealer().TryOpen(Kind, sealedValue, Binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void Without_a_configured_key_a_per_process_key_is_used()
    {
        var sealedValue = Sealer(key: null).Seal(Kind, Ticket, Binding);

        Sealer(key: string.Empty).TryOpen(Kind, sealedValue, Binding, out _).ShouldBeTrue();
        Sealer().TryOpen(Kind, sealedValue, Binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_delta_link_shaped_value_does_not_open() =>
        Sealer("k").TryOpen(Kind, "e30.c2ln", Binding, out _).ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("AQ")]
    [InlineData("AQID")]
    public void Garbage_does_not_open(string? value) =>
        Sealer().TryOpen(Kind, value, Binding, out _).ShouldBeFalse();

    [Fact]
    public void An_unknown_version_does_not_open()
    {
        var bytes = Base64Url.DecodeFromChars(Sealer().Seal(Kind, Ticket, Binding));
        bytes[0] = 2;

        Sealer().TryOpen(Kind, Base64Url.EncodeToString(bytes), Binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void Overlong_values_are_refused_unread() =>
        Sealer().TryOpen(Kind, new string('A', SelfAuthorisingUrlSealer.MaxSealedLength + 1), Binding, out _).ShouldBeFalse();

    [Theory]
    [InlineData("""{"t":"","e":9999999999}""")]
    [InlineData("""{"e":9999999999}""")]
    [InlineData("""{"t":"tok","e":999999999999999}""")]
    [InlineData("\"just a string\"")]
    public void A_sealed_payload_that_is_no_ticket_does_not_open(string json) =>
        Sealer().TryOpen(Kind, LegacySeal("shared-signing-key", "test/one", json, ["PUT", .. Binding]), Binding, out _).ShouldBeFalse();

    [Fact]
    public void Upload_urls_minted_before_the_generic_ticket_still_open()
    {
        // Phase 5B's UploadUrlProtector sealed {t, n, s, e} under this purpose, bound to PUT + the route.
        var legacy = LegacySeal("shared-signing-key", "nightware/todo-upload-url/v1",
            $$"""{"t":"header.payload.signature","n":"big.eml","s":4096,"e":{{Now.AddMinutes(15).ToUnixTimeSeconds()}}}""", ["PUT", .. Binding]);
        var upload = new SelfAuthorisingUrl("nightware/todo-upload-url/v1", HttpMethods.Put, TimeSpan.FromMinutes(15), static _ => null);

        Sealer().TryOpen(upload, legacy, Binding, out var ticket).ShouldBeTrue();
        ticket.ShouldBe(Ticket);
    }

    [Fact]
    public void A_ticket_lives_the_kinds_lifetime_the_tokens_or_the_callers_limit_whichever_is_first()
    {
        var download = new SelfAuthorisingUrl("test/download", "GET", TimeSpan.FromMinutes(5), static _ => null);

        Sealer().TicketFor(download, "t", Caller(exp: null)).ExpiresAt.ShouldBe(Now.AddMinutes(5));
        Sealer().TicketFor(download, "t", Caller(exp: Now.AddHours(1))).ExpiresAt.ShouldBe(Now.AddMinutes(5));
        Sealer().TicketFor(download, "t", Caller(exp: Now.AddMinutes(1))).ExpiresAt.ShouldBe(Now.AddMinutes(1));
        Sealer().TicketFor(download, "t", Caller(exp: Now.AddHours(1)), notAfter: Now.AddMinutes(2)).ExpiresAt.ShouldBe(Now.AddMinutes(2));
        Sealer().TicketFor(download, "t", Caller(exp: Now.AddMinutes(1)), notAfter: Now.AddMinutes(2)).ExpiresAt.ShouldBe(Now.AddMinutes(1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("99999999999999999")]
    public void An_unreadable_exp_claim_sets_no_limit(string? exp)
    {
        var caller = new ClaimsPrincipal(new ClaimsIdentity(exp is null ? [] : [new Claim("exp", exp)], "test"));

        SelfAuthorisingUrlSealer.ExpiryOf(caller).ShouldBeNull();
    }

    [Fact]
    public void A_purpose_is_required() =>
        Should.Throw<ArgumentException>(() => new SelfAuthorisingUrl(" ", "GET", TimeSpan.FromMinutes(1), static _ => null));

    private static ClaimsPrincipal Caller(DateTimeOffset? exp) =>
        new(new ClaimsIdentity(exp is { } e ? [new Claim("exp", e.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))] : [], "test"));

    /// <summary>The wire format, written out independently: base64url(1 ‖ nonce ‖ tag ‖ AES-GCM(json)), AAD = 1 ‖ (len ‖ field)*.</summary>
    private static string LegacySeal(string signingKey, string purpose, string json, string[] aadFields)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(signingKey), 32, salt: [], info: Encoding.UTF8.GetBytes(purpose));
        var fields = aadFields.Select(Encoding.UTF8.GetBytes).ToArray();
        var aad = new byte[1 + fields.Sum(f => 4 + f.Length)];
        aad[0] = 1;
        var offset = 1;
        foreach (var field in fields)
        {
            BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(offset), field.Length);
            field.CopyTo(aad, offset + 4);
            offset += 4 + field.Length;
        }

        var plaintext = Encoding.UTF8.GetBytes(json);
        var sealedBytes = new byte[29 + plaintext.Length];
        sealedBytes[0] = 1;
        RandomNumberGenerator.Fill(sealedBytes.AsSpan(1, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(sealedBytes.AsSpan(1, 12), plaintext, sealedBytes.AsSpan(29), sealedBytes.AsSpan(13, 16), aad);
        return Base64Url.EncodeToString(sealedBytes);
    }
}
