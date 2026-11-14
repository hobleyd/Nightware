using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Write;

public sealed class ReplyComposerTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EmailAddress Alex = new("alex@example.com", "Alex");
    private static readonly EmailAddress Sam = new("sam@example.com");
    private static readonly EmailAddress Kim = new("kim@example.com");
    private static readonly EmailAddress MeAddress = new("me@example.com", "Me");

    private static MailMessage Original(params InternetHeader[] headers) => new(new MessageId("e.orig"))
    {
        Subject = "Quarterly numbers",
        From = Alex,
        ToRecipients = [MeAddress, Sam],
        CcRecipients = [Kim, new EmailAddress("ME@example.com")],
        InternetMessageId = "orig@example.com",
        InternetMessageHeaders = headers,
    };

    private static DraftContent Compose(MailMessage original, ReplyKind kind, ComposedMessage? composed = null, MailboxRef? self = null) =>
        ReplyComposer.Compose(original, kind, composed ?? new ComposedMessage(), self ?? Me);

    private static string[] Addresses(IReadOnlyList<EmailAddress> list) => [.. list.Select(a => a.Address)];

    [Theory]
    [InlineData(ReplyKind.Reply, "Quarterly", "RE: Quarterly")]
    [InlineData(ReplyKind.ReplyAll, "Quarterly", "RE: Quarterly")]
    [InlineData(ReplyKind.Forward, "Quarterly", "FW: Quarterly")]
    [InlineData(ReplyKind.Reply, "Re: Quarterly", "Re: Quarterly")]
    [InlineData(ReplyKind.Reply, "RE : Quarterly", "RE : Quarterly")]
    [InlineData(ReplyKind.Reply, "  re:x", "re:x")]
    [InlineData(ReplyKind.Forward, "Fw: Quarterly", "Fw: Quarterly")]
    [InlineData(ReplyKind.Forward, "FWD: Quarterly", "FWD: Quarterly")]
    [InlineData(ReplyKind.Forward, "fwd:Quarterly", "fwd:Quarterly")]
    [InlineData(ReplyKind.Forward, "RE: Quarterly", "FW: RE: Quarterly")]
    [InlineData(ReplyKind.Reply, "FW: Quarterly", "RE: FW: Quarterly")]
    [InlineData(ReplyKind.Reply, "", "RE: ")]
    [InlineData(ReplyKind.Reply, null, "RE: ")]
    [InlineData(ReplyKind.Forward, null, "FW: ")]
    public void Prefixes_the_original_subject_once_when_none_was_composed(ReplyKind kind, string? original, string expected)
    {
        ReplyComposer.Subject(original, null, kind).ShouldBe(expected);
        ReplyComposer.Subject(original, "   ", kind).ShouldBe(expected);
    }

    [Fact]
    public void A_composed_subject_is_used_verbatim()
    {
        ReplyComposer.Subject("Quarterly", "my own words", ReplyKind.Reply).ShouldBe("my own words");
        Compose(Original(), ReplyKind.Forward, new ComposedMessage { Subject = "Look" }).Subject.ShouldBe("Look");
    }

    [Fact]
    public void Reply_goes_to_the_sender()
    {
        var content = Compose(Original(), ReplyKind.Reply);

        content.ToRecipients.ShouldBe([Alex]);
        content.CcRecipients.ShouldBeEmpty();
        content.BccRecipients.ShouldBeEmpty();
        content.AttachmentsFrom.ShouldBeNull();
        content.Subject.ShouldBe("RE: Quarterly numbers");
    }

    [Fact]
    public void Reply_to_wins_over_from()
    {
        var original = Original() with { ReplyTo = [Kim] };

        Compose(original, ReplyKind.Reply).ToRecipients.ShouldBe([Kim]);
    }

    [Fact]
    public void Reply_falls_back_to_the_sender_header_without_a_from()
    {
        var original = Original() with { From = null, Sender = Sam };

        Compose(original, ReplyKind.Reply).ToRecipients.ShouldBe([Sam]);
    }

    [Fact]
    public void Reply_without_from_or_sender_has_no_recipients()
    {
        Compose(Original() with { From = null }, ReplyKind.Reply).ToRecipients.ShouldBeEmpty();
    }

    [Fact]
    public void Reply_to_your_own_sent_message_goes_to_its_recipients()
    {
        var sent = Original() with { From = new EmailAddress(" ME@Example.com "), ToRecipients = [Alex, Sam] };

        Compose(sent, ReplyKind.Reply).ToRecipients.ShouldBe([Alex, Sam]);
    }

    [Fact]
    public void Reply_all_adds_the_original_recipients_without_the_mailbox()
    {
        var content = Compose(Original(), ReplyKind.ReplyAll);

        Addresses(content.ToRecipients).ShouldBe(["alex@example.com", "sam@example.com"]);
        Addresses(content.CcRecipients).ShouldBe(["kim@example.com"]);
    }

    [Fact]
    public void Reply_all_dedupes_case_insensitively_and_drops_cc_already_in_to()
    {
        var original = Original() with
        {
            ToRecipients = [MeAddress, new EmailAddress("ALEX@example.com"), Sam, new EmailAddress("sam@EXAMPLE.com")],
            CcRecipients = [new EmailAddress("Sam@example.com"), Kim, new EmailAddress("kim@example.com ")],
        };

        var content = Compose(original, ReplyKind.ReplyAll);

        Addresses(content.ToRecipients).ShouldBe(["alex@example.com", "sam@example.com"]);
        Addresses(content.CcRecipients).ShouldBe(["kim@example.com"]);
    }

    [Fact]
    public void Reply_all_compares_self_trimmed_and_case_insensitively()
    {
        var content = Compose(Original(), ReplyKind.ReplyAll, self: new MailboxRef(" Me@EXAMPLE.com "));

        content.ToRecipients.ShouldNotContain(a => a.Address.Equals("me@example.com", StringComparison.OrdinalIgnoreCase));
        content.CcRecipients.ShouldNotContain(a => a.Address.Equals("me@example.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reply_all_in_a_shared_mailbox_excludes_the_shared_address_not_the_caller()
    {
        var shared = new MailboxRef("team@example.com");
        var original = Original() with { ToRecipients = [new EmailAddress("team@example.com"), MeAddress] };

        var content = Compose(original, ReplyKind.ReplyAll, self: shared);

        Addresses(content.ToRecipients).ShouldBe(["alex@example.com", "me@example.com"]);
        Addresses(content.CcRecipients).ShouldBe(["kim@example.com"]); // ME@ is already in To
    }

    [Fact]
    public void Reply_all_to_your_own_sent_message_keeps_its_recipients()
    {
        var sent = Original() with { From = MeAddress, ToRecipients = [Alex, Sam], CcRecipients = [Kim] };

        var content = Compose(sent, ReplyKind.ReplyAll);

        content.ToRecipients.ShouldBe([Alex, Sam]);
        content.CcRecipients.ShouldBe([Kim]);
    }

    [Fact]
    public void Reply_all_to_a_message_you_sent_only_yourself_still_has_a_recipient()
    {
        var note = Original() with { From = MeAddress, ToRecipients = [MeAddress], CcRecipients = [] };

        Compose(note, ReplyKind.ReplyAll).ToRecipients.ShouldBe([MeAddress]);
    }

    [Theory]
    [InlineData(ReplyKind.Reply)]
    [InlineData(ReplyKind.ReplyAll)]
    [InlineData(ReplyKind.Forward)]
    public void Given_recipients_replace_the_defaults(ReplyKind kind)
    {
        var content = Compose(Original(), kind, new ComposedMessage { ToRecipients = [Kim], CcRecipients = [Sam], BccRecipients = [Alex] });

        content.ToRecipients.ShouldBe([Kim]);
        content.CcRecipients.ShouldBe([Sam]);
        content.BccRecipients.ShouldBe([Alex]);
    }

    [Fact]
    public void Reply_all_with_only_to_given_still_defaults_cc()
    {
        var content = Compose(Original(), ReplyKind.ReplyAll, new ComposedMessage { ToRecipients = [Sam] });

        content.ToRecipients.ShouldBe([Sam]);
        Addresses(content.CcRecipients).ShouldBe(["kim@example.com"]);
    }

    [Fact]
    public void Reply_all_with_only_cc_given_still_defaults_to()
    {
        var content = Compose(Original(), ReplyKind.ReplyAll, new ComposedMessage { CcRecipients = [Alex] });

        Addresses(content.ToRecipients).ShouldBe(["alex@example.com", "sam@example.com"]);
        content.CcRecipients.ShouldBe([Alex]);
    }

    [Fact]
    public void Empty_lists_count_as_not_given()
    {
        var content = Compose(Original(), ReplyKind.ReplyAll, new ComposedMessage { ToRecipients = [], CcRecipients = [], BccRecipients = [] });

        Addresses(content.ToRecipients).ShouldBe(["alex@example.com", "sam@example.com"]);
        Addresses(content.CcRecipients).ShouldBe(["kim@example.com"]);
        content.BccRecipients.ShouldBeEmpty();
    }

    [Fact]
    public void Forward_goes_only_to_the_composed_recipients_and_carries_the_attachments()
    {
        var original = Original(new InternetHeader("References", "<root@example.com>"));

        var content = Compose(original, ReplyKind.Forward, new ComposedMessage { ToRecipients = [Kim] });

        content.ToRecipients.ShouldBe([Kim]);
        content.CcRecipients.ShouldBeEmpty();
        content.AttachmentsFrom.ShouldBe(new MessageId("e.orig"));
        content.InReplyTo.ShouldBeNull();
        content.References.ShouldBe(["root@example.com", "orig@example.com"]);
        content.Subject.ShouldBe("FW: Quarterly numbers");
    }

    [Fact]
    public void Forward_without_recipients_has_none()
    {
        Compose(Original(), ReplyKind.Forward).ToRecipients.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("orig@example.com")]
    [InlineData("<orig@example.com>")]
    [InlineData("  <orig@example.com> ")]
    public void Reply_answers_the_original_message_id_without_brackets(string messageId)
    {
        var content = Compose(Original() with { InternetMessageId = messageId }, ReplyKind.Reply);

        content.InReplyTo.ShouldBe("orig@example.com");
        content.References.ShouldBe(["orig@example.com"]);
    }

    [Fact]
    public void References_extend_the_original_chain()
    {
        var original = Original(new InternetHeader("references", "<a@x> <b@x>\r\n <c@x>"), new InternetHeader("In-Reply-To", "<c@x>"));

        Compose(original, ReplyKind.Reply).References.ShouldBe(["a@x", "b@x", "c@x", "orig@example.com"]);
    }

    [Fact]
    public void The_last_references_header_counts()
    {
        var original = Original(new InternetHeader("References", "<old@x>"), new InternetHeader("REFERENCES", "<new@x>"));

        Compose(original, ReplyKind.Reply).References.ShouldBe(["new@x", "orig@example.com"]);
    }

    [Fact]
    public void Without_references_a_single_in_reply_to_is_the_chain()
    {
        var original = Original(new InternetHeader("In-Reply-To", "<parent@x>"));

        Compose(original, ReplyKind.Reply).References.ShouldBe(["parent@x", "orig@example.com"]);
    }

    [Fact]
    public void Without_references_several_in_reply_to_ids_are_ignored()
    {
        var original = Original(new InternetHeader("In-Reply-To", "<p1@x> <p2@x>"));

        Compose(original, ReplyKind.Reply).References.ShouldBe(["orig@example.com"]);
    }

    [Fact]
    public void Duplicate_references_are_removed()
    {
        var original = Original(new InternetHeader("References", "<a@x> <a@x> <orig@example.com>"));

        Compose(original, ReplyKind.Reply).References.ShouldBe(["a@x", "orig@example.com"]);
    }

    [Fact]
    public void A_long_chain_keeps_the_root_and_the_most_recent_ancestors()
    {
        var ids = Enumerable.Range(1, 30).Select(i => $"m{i}@x").ToList();
        var original = Original(new InternetHeader("References", string.Join(' ', ids.Select(i => $"<{i}>"))));

        var references = Compose(original, ReplyKind.Reply).References;

        references.Count.ShouldBe(ReplyComposer.MaxReferences);
        references[0].ShouldBe("m1@x");
        references[^1].ShouldBe("orig@example.com");
        references.Skip(1).ShouldBe([.. ids.Skip(12), "orig@example.com"]);
    }

    [Fact]
    public void An_original_without_message_id_threads_on_its_ancestors_only()
    {
        var original = Original(new InternetHeader("References", "<a@x>")) with { InternetMessageId = null };

        var content = Compose(original, ReplyKind.Reply);

        content.InReplyTo.ShouldBeNull();
        content.References.ShouldBe(["a@x"]);
    }

    [Fact]
    public void An_original_without_any_ids_gives_no_threading()
    {
        var content = Compose(Original() with { InternetMessageId = "" }, ReplyKind.Reply);

        content.InReplyTo.ShouldBeNull();
        content.References.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(BodyType.Html, "<p>Thanks</p><blockquote>Hi</blockquote>")]
    [InlineData(BodyType.Text, "Thanks\n\n> Hi")]
    public void The_composed_body_is_used_verbatim(BodyType type, string text)
    {
        var body = new ItemBody(type, text);

        foreach (var kind in Enum.GetValues<ReplyKind>())
        {
            Compose(Original(), kind, new ComposedMessage { Body = body }).Body.ShouldBe(body);
        }
    }

    [Fact]
    public void Without_a_body_the_reply_is_empty_html_and_never_quotes_the_original()
    {
        var original = Original() with { Body = new ItemBody(BodyType.Html, "<p>original text</p>") };

        Compose(original, ReplyKind.Reply).Body.ShouldBe(new ItemBody(BodyType.Html, string.Empty));
    }

    [Fact]
    public void Original_fetch_selects_what_compose_reads()
    {
        ReplyComposer.OriginalFetch.Select.ShouldNotBeNull();
        ReplyComposer.OriginalFetch.Select.ShouldBe(
            ["subject", "from", "sender", "replyTo", "toRecipients", "ccRecipients", "internetMessageId", "internetMessageHeaders"],
            ignoreOrder: true);
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("no brackets here", new string[0])]
    [InlineData("<a@x>", new[] { "a@x" })]
    [InlineData(" <a@x>\r\n\t<b@y> junk <c@z>", new[] { "a@x", "b@y", "c@z" })]
    [InlineData("<has space@x> <ok@x>", new[] { "ok@x" })]
    [InlineData("<<nested@x>>", new[] { "nested@x" })]
    public void Message_ids_are_read_from_angle_brackets(string? header, string[] expected)
    {
        ReplyComposer.MessageIds(header).ShouldBe(expected);
    }

    [Fact]
    public void Rejects_an_unknown_kind_and_null_arguments()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Compose(Original(), (ReplyKind)42));
        Should.Throw<ArgumentNullException>(() => ReplyComposer.Compose(null!, ReplyKind.Reply, new ComposedMessage(), Me));
        Should.Throw<ArgumentNullException>(() => ReplyComposer.Compose(Original(), ReplyKind.Reply, null!, Me));
    }
}
