using Nightware.Adapters.Jmap.Core;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Core;

public sealed class JmapDeltaTokenCodecTests
{
    [Fact]
    public void Changes_cursor_has_a_stable_wire_form()
    {
        JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("a1", "s42")).Value
            .ShouldBe("eyJ2IjoxLCJtIjoiYTEiLCJwIjoiYyIsInMiOiJzNDIifQ");
    }

    [Fact]
    public void Initial_cursor_round_trips()
    {
        var cursor = JmapDeltaCursor.Initial("inbox-id", "s7", 100) with { Select = ["id", "subject"], Top = 25 };

        var decoded = JmapDeltaTokenCodec.Decode(JmapDeltaTokenCodec.Encode(cursor));

        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.MailboxId.ShouldBe("inbox-id");
        decoded.Value.Phase.ShouldBe(DeltaPhase.Initial);
        decoded.Value.Position.ShouldBe(100);
        decoded.Value.State.ShouldBe("s7");
        decoded.Value.Select.ShouldBe(["id", "subject"]);
        decoded.Value.Top.ShouldBe(25);
    }

    [Fact]
    public void Changes_cursor_round_trips()
    {
        var decoded = JmapDeltaTokenCodec.Decode(JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("m", "state/with+chars")));

        decoded.Value.Phase.ShouldBe(DeltaPhase.Changes);
        decoded.Value.State.ShouldBe("state/with+chars");
        decoded.Value.Position.ShouldBeNull();
    }

    [Fact]
    public void The_query_state_round_trips_and_is_optional()
    {
        JmapDeltaTokenCodec.Decode(JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("m", "s", "q7"))).Value.QueryState.ShouldBe("q7");
        JmapDeltaTokenCodec.Decode(JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Initial("m", "s", 5, "q8"))).Value.QueryState.ShouldBe("q8");
        JmapDeltaTokenCodec.Decode(new DeltaToken("eyJ2IjoxLCJtIjoiYTEiLCJwIjoiYyIsInMiOiJzNDIifQ")).Value.QueryState.ShouldBeNull("tokens issued before it still decode");
    }

    [Fact]
    public void Tokens_are_url_safe()
    {
        var token = JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("m?>>", "s~~~???")).Value;

        token.ShouldNotContain("+");
        token.ShouldNotContain("/");
        token.ShouldNotContain("=");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !")]
    [InlineData("bm90IGpzb24")] // "not json"
    [InlineData("eyJ2IjoyLCJtIjoiYTEiLCJwIjoiYyIsInMiOiJzNDIifQ")] // v2
    [InlineData("eyJ2IjoxLCJwIjoiYyIsInMiOiJzNDIifQ")] // no mailbox
    [InlineData("eyJ2IjoxLCJtIjoiYTEiLCJwIjoiYyJ9")] // changes without state
    [InlineData("eyJ2IjoxLCJtIjoiYTEiLCJwIjoiaSJ9")] // initial without position
    [InlineData("eyJ2IjoxLCJtIjoiYTEiLCJwIjoieCIsInMiOiJzIn0")] // unknown phase
    [InlineData("bnVsbA")] // null
    public void Undecodable_or_old_tokens_mean_sync_state_gone(string token)
    {
        var decoded = JmapDeltaTokenCodec.Decode(new DeltaToken(token));

        decoded.IsSuccess.ShouldBeFalse();
        decoded.Error.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Fact]
    public void Default_token_is_gone()
    {
        JmapDeltaTokenCodec.Decode(default(DeltaToken)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Fact]
    public void A_token_for_another_folder_is_gone()
    {
        var token = JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("a1", "s"));

        JmapDeltaTokenCodec.Decode(token, "a1").IsSuccess.ShouldBeTrue();
        JmapDeltaTokenCodec.Decode(token, "b2").Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Fact]
    public void Encode_rejects_incomplete_cursors()
    {
        Should.Throw<ArgumentException>(() => JmapDeltaTokenCodec.Encode(new JmapDeltaCursor("m", DeltaPhase.Initial, null, null)));
        Should.Throw<ArgumentException>(() => JmapDeltaTokenCodec.Encode(new JmapDeltaCursor("m", DeltaPhase.Initial, -1, null)));
        Should.Throw<ArgumentException>(() => JmapDeltaTokenCodec.Encode(new JmapDeltaCursor("m", DeltaPhase.Changes, null, "")));
        Should.Throw<ArgumentException>(() => JmapDeltaTokenCodec.Encode(new JmapDeltaCursor("", DeltaPhase.Changes, null, "s")));
    }

    [Fact]
    public void The_anchor_round_trips_in_the_initial_phase_only()
    {
        JmapDeltaTokenCodec.Decode(JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Initial("m", "s", 5, "q", "e9"))).Value.Anchor.ShouldBe("e9");
        JmapDeltaTokenCodec.Decode(JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("m", "s") with { Anchor = "e9" })).Value.Anchor.ShouldBeNull();
        JmapDeltaTokenCodec.Decode(JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Initial("m", "s", 5))).Value.Anchor.ShouldBeNull("tokens without one still decode");
    }
}
