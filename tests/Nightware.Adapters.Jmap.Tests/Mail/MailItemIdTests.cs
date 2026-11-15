using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Mail;

public sealed class MailItemIdTests
{
    [Fact]
    public void Email_ids_format_with_the_e_prefix()
    {
        MailItemId.ForEmail("Mab12_-Z").ToString().ShouldBe("e.Mab12_-Z");
    }

    [Theory]
    [InlineData("abc@example.com", "d.YWJjQGV4YW1wbGUuY29t")]
    [InlineData("<abc@example.com>", "d.YWJjQGV4YW1wbGUuY29t")]
    [InlineData("0193a1b2c3d4e5f6@nightware.localhost", "d.MDE5M2ExYjJjM2Q0ZTVmNkBuaWdodHdhcmUubG9jYWxob3N0")]
    [InlineData("a", "d.YQ")]
    public void Draft_ids_are_unpadded_base64url_of_the_bare_message_id(string messageId, string expected)
    {
        MailItemId.ForDraft(messageId).ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData("e.Mab12")]
    [InlineData("d.YWJjQGV4YW1wbGUuY29t")]
    [InlineData("d.YQ")]
    public void Parse_round_trips_canonical_ids(string id)
    {
        var parsed = MailItemId.Parse(new MessageId(id));

        parsed.IsSuccess.ShouldBeTrue();
        parsed.Value.ToMessageId().Value.ShouldBe(id);
    }

    [Fact]
    public void Parse_exposes_kind_and_value()
    {
        var draft = MailItemId.Parse(new MessageId("d.YWJjQGV4YW1wbGUuY29t")).Value;
        draft.Kind.ShouldBe(MailItemKind.Draft);
        draft.IsDraft.ShouldBeTrue();
        draft.Value.ShouldBe("abc@example.com");

        var email = MailItemId.Parse(new MessageId("e.M42")).Value;
        email.Kind.ShouldBe(MailItemKind.Email);
        email.Value.ShouldBe("M42");
    }

    [Theory]
    [InlineData("")]
    [InlineData("M42")]
    [InlineData("x.M42")]
    [InlineData("e.")]
    [InlineData("e.has space")]
    [InlineData("e.dot.ted")]
    [InlineData("E.M42")]
    [InlineData("d.")]
    [InlineData("d.YQ==")]
    [InlineData("d.YR")]
    [InlineData("d.Y Q")]
    [InlineData("d.!!!")]
    [InlineData("d.PGFiY0BleGFtcGxlLmNvbT4")]
    [InlineData("d.YSBi")]
    public void Parse_rejects_non_canonical_or_malformed_ids(string id)
    {
        var parsed = MailItemId.Parse(new MessageId(id));

        parsed.IsSuccess.ShouldBeFalse();
        parsed.Error.Kind.ShouldBe(ErrorKind.Validation);
        parsed.Error.Code.ShouldBe("ErrorInvalidIdMalformed");
    }

    [Fact]
    public void Parse_rejects_a_default_message_id()
    {
        MailItemId.Parse(default).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Parse_rejects_jmap_ids_longer_than_255()
    {
        MailItemId.Parse(new MessageId("e." + new string('a', 256))).IsSuccess.ShouldBeFalse();
        MailItemId.Parse(new MessageId("e." + new string('a', 255))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Parse_rejects_invalid_utf8_payloads()
    {
        // 0xFF 0xFE is not UTF-8.
        MailItemId.Parse(new MessageId("d._-4")).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Constructors_reject_invalid_values()
    {
        Should.Throw<ArgumentException>(() => MailItemId.ForEmail("bad id"));
        Should.Throw<ArgumentException>(() => MailItemId.ForDraft("<>"));
        Should.Throw<ArgumentException>(() => MailItemId.ForDraft("two words@x"));
    }

    [Fact]
    public void For_emits_the_draft_form_for_drafts_with_one_message_id()
    {
        var keywords = new Dictionary<string, bool> { [Keywords.Draft] = true, [Keywords.Seen] = true };

        MailItemId.For("M1", keywords, ["abc@example.com"]).ToString().ShouldBe("d.YWJjQGV4YW1wbGUuY29t");
    }

    [Fact]
    public void For_emits_the_email_form_for_ordinary_mail()
    {
        var keywords = new Dictionary<string, bool> { [Keywords.Seen] = true };

        MailItemId.For("M1", keywords, ["abc@example.com"]).ToString().ShouldBe("e.M1");
        MailItemId.For("M1", null, null).ToString().ShouldBe("e.M1");
    }

    [Fact]
    public void For_falls_back_to_the_email_form_for_drafts_without_a_unique_message_id()
    {
        var keywords = new Dictionary<string, bool> { [Keywords.Draft] = true };

        MailItemId.For("M1", keywords, null).ToString().ShouldBe("e.M1");
        MailItemId.For("M1", keywords, []).ToString().ShouldBe("e.M1");
        MailItemId.For("M1", keywords, ["a@x", "b@x"]).ToString().ShouldBe("e.M1");
        MailItemId.For("M1", keywords, ["not valid"]).ToString().ShouldBe("e.M1");
    }

    [Fact]
    public void For_accepts_an_email_model()
    {
        var email = new Email { Id = "M9", Keywords = new Dictionary<string, bool> { [Keywords.Draft] = true }, MessageId = ["a"] };

        MailItemId.For(email).ToString().ShouldBe("d.YQ");
        Should.Throw<ArgumentException>(() => MailItemId.For(new Email()));
    }

    [Fact]
    public void MatchesDraft_requires_an_exact_message_id()
    {
        var id = MailItemId.ForDraft("abc@example.com");

        id.MatchesDraft(new Email { MessageId = ["abc@example.com"] }).ShouldBeTrue();
        id.MatchesDraft(new Email { MessageId = ["<abc@example.com>"] }).ShouldBeTrue();
        id.MatchesDraft(new Email { MessageId = ["xabc@example.com"] }).ShouldBeFalse();
        id.MatchesDraft(new Email { MessageId = ["ABC@example.com"] }).ShouldBeFalse();
        id.MatchesDraft(new Email()).ShouldBeFalse();
        MailItemId.ForEmail("M1").MatchesDraft(new Email { MessageId = ["abc@example.com"] }).ShouldBeFalse();
    }
}
