using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Nightware.Api.Features.Files.Download;
using Nightware.Api.Features.Tasks.Upload;
using Nightware.Api.Graph.Json;
using Nightware.Domain.Files;

namespace Nightware.Api.Tests.Features.Files;

public sealed class DownloadUrlProtectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DownloadUrlBinding Binding = new("u!aHR0cHM6Ly9maWxlcy5leGFtcGxlLmNvbS9zL0FiQ2RFZkdo", ContentFormat.Pdf);

    private readonly FakeTimeProvider _clock = new(Now);

    private DownloadUrlProtector Protector(string? key = "shared-signing-key") =>
        new(Options.Create(new DeltaLinkOptions { SigningKey = key }), _clock);

    private static DownloadTicket Ticket => new("header.payload.signature", Now.AddMinutes(5));

    [Fact]
    public void A_sealed_ticket_opens_for_its_own_binding_on_any_instance()
    {
        var sealedValue = Protector().Seal(Ticket, Binding);

        Protector().TryOpen(sealedValue, Binding, out var ticket).ShouldBeTrue();
        ticket.ShouldBe(Ticket);
        sealedValue.ShouldNotContain("payload", Case.Insensitive);
    }

    [Fact]
    public void Another_share_id_or_format_does_not_open_it()
    {
        var sealedValue = Protector().Seal(Ticket, Binding);

        Protector().TryOpen(sealedValue, Binding with { ShareId = "u!b3RoZXI" }, out _).ShouldBeFalse();
        Protector().TryOpen(sealedValue, Binding with { Format = ContentFormat.Original }, out _).ShouldBeFalse();
    }

    [Fact]
    public void Another_key_does_not_open_it() =>
        Protector("other").TryOpen(Protector().Seal(Ticket, Binding), Binding, out _).ShouldBeFalse();

    [Fact]
    public void Without_a_key_a_per_process_key_still_round_trips() =>
        Protector(null).TryOpen(Protector(null).Seal(Ticket, Binding), Binding, out _).ShouldBeTrue();

    [Fact]
    public void It_stops_working_when_it_expires()
    {
        var sealedValue = Protector().Seal(Ticket, Binding);
        _clock.Advance(TimeSpan.FromMinutes(5));

        Protector().TryOpen(sealedValue, Binding, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("AQID")]
    public void Garbage_does_not_open(string? value) =>
        Protector().TryOpen(value, Binding, out _).ShouldBeFalse();

    [Fact]
    public void An_upload_url_credential_never_opens_as_a_download()
    {
        var upload = new UploadUrlProtector(Options.Create(new DeltaLinkOptions { SigningKey = "shared-signing-key" }), _clock);
        var uploadBinding = new UploadUrlBinding("GET", Binding.ShareId, "pdf", "x");
        var sealedValue = upload.Seal(new UploadTicket("tok", "n", 1, Now.AddMinutes(5)), uploadBinding);

        Protector().TryOpen(sealedValue, Binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_ticket_lives_five_minutes_or_until_the_token_expires()
    {
        Protector().TicketFor("t", null).ExpiresAt.ShouldBe(Now + DownloadUrl.Lifetime);
        Protector().TicketFor("t", Now.AddHours(1)).ExpiresAt.ShouldBe(Now + DownloadUrl.Lifetime);
        Protector().TicketFor("t", Now.AddMinutes(1)).ExpiresAt.ShouldBe(Now.AddMinutes(1));
    }
}
