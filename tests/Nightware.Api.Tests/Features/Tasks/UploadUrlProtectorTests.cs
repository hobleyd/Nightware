using System.Buffers.Text;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Nightware.Api.Features.Tasks.Upload;
using Nightware.Api.Graph.Json;

namespace Nightware.Api.Tests.Features.Tasks;

public sealed class UploadUrlProtectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly UploadUrlBinding Binding = new("alice@example.com", "list-1", "task-1", "session-1");
    private static readonly UploadTicket Ticket = new("header.payload.signature", "big.eml", 4096, Now.AddMinutes(15));

    private readonly FakeTimeProvider _clock = new(Now);

    private UploadUrlProtector Protector(string? key = "shared-signing-key") =>
        new(Options.Create(new DeltaLinkOptions { SigningKey = key }), _clock);

    [Fact]
    public void A_sealed_ticket_opens_for_its_own_binding()
    {
        var sealedValue = Protector().Seal(Ticket, Binding);

        Protector().TryOpen(sealedValue, Binding, out var ticket).ShouldBeTrue();
        ticket.ShouldBe(Ticket);
        sealedValue.ShouldNotContain("payload", Case.Insensitive);
        sealedValue.ShouldNotContain("=");
    }

    [Fact]
    public void Another_instance_with_the_same_key_opens_it() =>
        Protector().TryOpen(Protector().Seal(Ticket, Binding), Binding, out _).ShouldBeTrue();

    [Fact]
    public void The_mailbox_compares_case_insensitively() =>
        Protector().TryOpen(Protector().Seal(Ticket, Binding), Binding with { Mailbox = "Alice@Example.com" }, out _).ShouldBeTrue();

    [Fact]
    public void Every_seal_uses_a_fresh_nonce() =>
        Protector().Seal(Ticket, Binding).ShouldNotBe(Protector().Seal(Ticket, Binding));

    [Theory]
    [InlineData("mailbox")]
    [InlineData("list")]
    [InlineData("task")]
    [InlineData("session")]
    [InlineData("shifted")]
    public void It_does_not_open_for_any_other_route(string changed)
    {
        var other = changed switch
        {
            "mailbox" => Binding with { Mailbox = "bob@example.com" },
            "list" => Binding with { ListId = "list-2" },
            "task" => Binding with { TaskId = "task-2" },
            "session" => Binding with { SessionId = "session-2" },
            _ => new UploadUrlBinding("alice@example.com", "list-1task-1", string.Empty, "session-1"),
        };

        Protector().TryOpen(Protector().Seal(Ticket, Binding), other, out _).ShouldBeFalse();
    }

    [Fact]
    public void Flipping_any_byte_breaks_it()
    {
        var bytes = Base64Url.DecodeFromChars(Protector().Seal(Ticket, Binding));
        for (var i = 0; i < bytes.Length; i++)
        {
            var tampered = (byte[])bytes.Clone();
            tampered[i] ^= 0x01;

            Protector().TryOpen(Base64Url.EncodeToString(tampered), Binding, out _).ShouldBeFalse($"byte {i}");
        }
    }

    [Fact]
    public void A_truncated_value_does_not_open()
    {
        var sealedValue = Protector().Seal(Ticket, Binding);

        foreach (var length in new[] { 0, 1, 10, 28, 29, sealedValue.Length - 1 })
        {
            Protector().TryOpen(sealedValue[..length], Binding, out _).ShouldBeFalse($"length {length}");
        }
    }

    [Fact]
    public void It_expires()
    {
        var sealedValue = Protector().Seal(Ticket, Binding);
        _clock.Advance(TimeSpan.FromMinutes(15));

        Protector().TryOpen(sealedValue, Binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void Another_key_cannot_open_it() =>
        Protector("another-key").TryOpen(Protector().Seal(Ticket, Binding), Binding, out _).ShouldBeFalse();

    [Fact]
    public void Without_a_configured_key_a_per_process_key_is_used()
    {
        var sealedValue = Protector(key: null).Seal(Ticket, Binding);

        Protector(key: string.Empty).TryOpen(sealedValue, Binding, out _).ShouldBeTrue();
        Protector().TryOpen(sealedValue, Binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void The_key_is_not_the_delta_link_key()
    {
        // A value signed by the delta-link HMAC must never pass as an upload credential, and vice versa.
        var sealedValue = Protector("k").Seal(Ticket, Binding);

        sealedValue.ShouldNotContain(".");
        Protector("k").TryOpen("e30.c2ln", Binding, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("AQ")]
    public void Garbage_does_not_open(string? value) =>
        Protector().TryOpen(value, Binding, out _).ShouldBeFalse();

    [Fact]
    public void An_unknown_version_does_not_open()
    {
        var bytes = Base64Url.DecodeFromChars(Protector().Seal(Ticket, Binding));
        bytes[0] = 2;

        Protector().TryOpen(Base64Url.EncodeToString(bytes), Binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void Overlong_values_are_refused_unread() =>
        Protector().TryOpen(new string('A', UploadUrlProtector.MaxSealedLength + 1), Binding, out _).ShouldBeFalse();

    [Fact]
    public void A_realistic_access_token_fits_the_request_line()
    {
        var keycloakSized = new string('x', 2600);
        var binding = new UploadUrlBinding("alice@nightware.localhost", new string('L', 90), new string('T', 140), new string('S', 420));

        var url = UploadUrl.Build("https://graph.nightware.localhost/v1.0", binding, Protector().Seal(Ticket with { BearerToken = keycloakSized }, binding));

        // Leaves room for an access token twice this size before the request line overflows.
        url.Length.ShouldBeLessThan(UploadUrl.MaxRequestLine * 2 / 3);
    }
}
