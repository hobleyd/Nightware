using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Nightware.Api.Graph.Json;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Graph.Json;

public sealed class DeltaLinkCodecTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));

    private DeltaLinkCodec Codec(string? key = "key-1", TimeSpan? lifetime = null) =>
        new(Options.Create(new DeltaLinkOptions { SigningKey = key, Lifetime = lifetime ?? TimeSpan.FromDays(30) }), _clock, NullLogger<DeltaLinkCodec>.Instance);

    [Fact]
    public void Round_trips_token_select_and_top()
    {
        var codec = Codec();
        var state = new DeltaLinkState(new DeltaToken("eyJ2IjoxfQ"), new HashSet<string> { "id", "subject" }, 50);

        var decoded = codec.Decode(codec.Encode(state));

        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.Token.ShouldBe(state.Token);
        decoded.Value.Select!.Order().ShouldBe(["id", "subject"]);
        decoded.Value.Top.ShouldBe(50);
    }

    [Fact]
    public void Round_trips_without_select_or_top()
    {
        var codec = Codec();

        var decoded = codec.Decode(codec.Encode(new DeltaLinkState(new DeltaToken("t"), null, null))).Value;

        decoded.Select.ShouldBeNull();
        decoded.Top.ShouldBeNull();
    }

    [Fact]
    public void Encoded_links_are_url_safe_without_padding()
    {
        var link = Codec().Encode(new DeltaLinkState(new DeltaToken("+/=?&"), new HashSet<string> { "id" }, 1));

        link.ShouldNotContain("+");
        link.ShouldNotContain("/");
        link.ShouldNotContain("=");
        Uri.EscapeDataString(link).ShouldBe(link);
    }

    [Fact]
    public void A_payload_that_fails_its_signature_is_gone()
    {
        var codec = Codec();
        var link = codec.Encode(new DeltaLinkState(new DeltaToken("t"), null, 50));
        var dot = link.IndexOf('.', StringComparison.Ordinal);
        var forged = Base64Url(Encoding.UTF8.GetBytes("{\"v\":1,\"t\":\"other\",\"i\":0}")) + link[dot..];

        var result = codec.Decode(forged);

        result.Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Fact]
    public void A_link_signed_with_another_key_is_gone_so_a_rotation_resyncs()
    {
        var link = Codec("key-1").Encode(new DeltaLinkState(new DeltaToken("t"), null, null));

        Codec("key-2").Decode(link).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dot")]
    [InlineData(".sig")]
    [InlineData("payload.")]
    [InlineData("!!!.###")]
    public void Undecodable_links_are_a_410(string? link)
    {
        var result = Codec().Decode(link);

        result.Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
        result.Error.Code.ShouldBe("syncStateNotFound");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":2,\"t\":\"x\",\"i\":0}")]
    [InlineData("{\"v\":1,\"i\":0}")]
    public void Correctly_signed_but_unusable_payloads_are_a_410(string payload)
    {
        var json = Encoding.UTF8.GetBytes(payload);
        var signature = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes("key-1"), json);

        Codec().Decode($"{Base64Url(json)}.{Base64Url(signature)}").Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Fact]
    public void Expired_links_are_a_410()
    {
        var codec = Codec(lifetime: TimeSpan.FromDays(7));
        var link = codec.Encode(new DeltaLinkState(new DeltaToken("t"), null, null));

        _clock.Advance(TimeSpan.FromDays(7));
        codec.Decode(link).IsSuccess.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromSeconds(1));
        codec.Decode(link).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Fact]
    public void Without_a_configured_key_links_still_round_trip_within_the_process()
    {
        var codec = Codec(key: null);

        codec.Decode(codec.Encode(new DeltaLinkState(new DeltaToken("t"), null, null))).IsSuccess.ShouldBeTrue();
        Codec(key: null).Decode(codec.Encode(new DeltaLinkState(new DeltaToken("t"), null, null))).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void ReadCursor_starts_an_initial_sync_from_the_request_options()
    {
        var query = new ItemQuery { Select = new HashSet<string> { "id" }, Top = 50 };

        var cursor = Codec().ReadCursor(new QueryCollection(), query).Value;

        cursor.IsInitial.ShouldBeTrue();
        cursor.Select.ShouldBe(query.Select);
        cursor.Top.ShouldBe(50);
    }

    [Theory]
    [InlineData("$deltatoken")]
    [InlineData("$skiptoken")]
    public void ReadCursor_takes_frozen_options_from_either_token_parameter(string parameter)
    {
        var codec = Codec();
        var link = codec.Encode(new DeltaLinkState(new DeltaToken("adapter"), new HashSet<string> { "id", "isRead" }, 50));
        var query = new QueryCollection(QueryHelpers.ParseQuery($"{parameter}={link}"));

        var cursor = codec.ReadCursor(query, new ItemQuery { Select = new HashSet<string> { "ignored" }, Top = 7 }).Value;

        cursor.IsInitial.ShouldBeFalse();
        cursor.Token.ShouldBe(new DeltaToken("adapter"));
        cursor.Select!.Order().ShouldBe(["id", "isRead"]);
        cursor.Top.ShouldBe(50);
    }

    [Fact]
    public void ReadCursor_surfaces_decode_errors()
    {
        var query = new QueryCollection(QueryHelpers.ParseQuery("$deltatoken=garbage"));

        Codec().ReadCursor(query, ItemQuery.Default).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
