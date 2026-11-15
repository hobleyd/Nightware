using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Adapters.Jmap.Mail.Read;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class EmailMapperTests
{
    [Fact]
    public void Maps_a_recorded_invitation()
    {
        var message = EmailMapper.ToMessage(MailReadStubs.RecordedInvitation());

        message.Id.ShouldBe(new MessageId("e.eaaaaab"));
        message.Subject.ShouldStartWith("Invitation: Nightware iMIP smoke");
        message.From.ShouldBe(new EmailAddress("calendar@nightware.localhost", "Alice Anderson via Nextcloud"));
        message.ToRecipients.ShouldBe([new EmailAddress("bob@nightware.localhost")]);
        message.ConversationId.ShouldBe("b");
        message.ParentFolderId.ShouldBe(new FolderId("a"));
        message.InternetMessageId.ShouldBe("0cabbeae49ca9d342f2312533fe8d134@nightware.localhost");
        message.IsRead.ShouldBeFalse();
        message.Categories.ShouldBeEmpty(); // $hasnoattachment is a system keyword
        message.CalendarPart.ShouldBe(new CalendarPart("REQUEST", AttachmentItemId.Assign(MailReadStubs.RecordedInvitation().BodyStructure)["6"].ToAttachmentId()));
        message.Body!.ContentType.ShouldBe(BodyType.Html);
        message.Body.Content.ShouldContain("<html", Case.Insensitive);
    }

    [Fact]
    public void The_invitation_lists_its_inline_logo_and_its_ics_as_a_file()
    {
        var attachments = EmailMapper.ToMessage(MailReadStubs.RecordedInvitation()).Attachments;

        var ids = AttachmentItemId.Assign(MailReadStubs.RecordedInvitation().BodyStructure);
        attachments.Select(a => (a.Id.Value, a.Name, a.ContentType, a.IsInline, a.ContentId)).ShouldBe(
        [
            (ids["5"].ToString(), "logo", "image/png", true, "29ddc05e4ad796b584ab205c05db7b15@symfony"),
            (ids["6"].ToString(), "event.ics", "text/calendar", false, null),
        ]);
        attachments.ShouldAllBe(a => a.Kind == AttachmentKind.File);
    }

    [Fact]
    public void Prefer_text_returns_the_plain_alternative()
    {
        var message = EmailMapper.ToMessage(MailReadStubs.RecordedInvitation(), preferredBody: BodyType.Text);

        message.Body!.ContentType.ShouldBe(BodyType.Text);
        message.Body.Content.ShouldNotContain("<");
    }

    [Fact]
    public void Plain_text_mail_is_a_text_body_even_though_html_body_falls_back_to_it()
    {
        // RFC 8621 puts the text/plain part in htmlBody too when there is no HTML (recording 04).
        var email = MailReadStubs.Parse(MailReadStubs.Email("m1")) with
        {
            TextBody = [new() { PartId = "1", Type = "text/plain" }],
            HtmlBody = [new() { PartId = "1", Type = "text/plain" }],
            BodyValues = new Dictionary<string, EmailBodyValue> { ["1"] = new("line 1\r\nline 2") },
        };

        EmailMapper.ToMessage(email).Body.ShouldBe(new ItemBody(BodyType.Text, "line 1\r\nline 2"));
        EmailMapper.ToMessage(email, preferredBody: BodyType.Html).Body.ShouldBe(new ItemBody(BodyType.Html, "<div>line 1<br>line 2</div>"));
    }

    [Fact]
    public void Html_only_mail_is_converted_when_text_is_preferred()
    {
        var email = MailReadStubs.Parse(MailReadStubs.Email("m1")) with
        {
            TextBody = [new() { PartId = "1", Type = "text/html" }],
            HtmlBody = [new() { PartId = "1", Type = "text/html" }],
            BodyValues = new Dictionary<string, EmailBodyValue> { ["1"] = new("<p>Hello <b>you</b></p><p>Bye</p>") },
        };

        EmailMapper.ToMessage(email).Body.ShouldBe(new ItemBody(BodyType.Html, "<p>Hello <b>you</b></p><p>Bye</p>"));
        EmailMapper.ToMessage(email, preferredBody: BodyType.Text).Body.ShouldBe(new ItemBody(BodyType.Text, "Hello you\r\nBye"));
    }

    [Fact]
    public void A_text_part_between_html_parts_is_escaped_into_the_html_body()
    {
        var email = MailReadStubs.Parse(MailReadStubs.Email("m1")) with
        {
            HtmlBody = [new() { PartId = "1", Type = "text/html" }, new() { PartId = "2", Type = "text/plain" }, new() { PartId = "3", Type = "image/png" }],
            BodyValues = new Dictionary<string, EmailBodyValue> { ["1"] = new("<p>a</p>"), ["2"] = new("b < c") },
        };

        EmailMapper.ToMessage(email).Body!.Content.ShouldBe("<p>a</p><div>b &lt; c</div>");
    }

    [Fact]
    public void A_list_projection_has_no_body()
    {
        EmailMapper.ToMessage(MailReadStubs.Parse(MailReadStubs.Email("m1"))).Body.ShouldBeNull();
    }

    [Fact]
    public void Keywords_map_to_read_flag_draft_and_categories()
    {
        var email = MailReadStubs.Parse(MailReadStubs.Email("m1", keywords: """{"$seen":true,"$flagged":true,"Project X":true,"$junk":true,"off":false}"""));

        var message = EmailMapper.ToMessage(email);

        message.IsRead.ShouldBeTrue();
        message.Flag.Status.ShouldBe(FlagStatus.Flagged);
        message.IsDraft.ShouldBeFalse();
        message.Categories.ShouldBe(["Project X"]);
    }

    [Fact]
    public void A_draft_gets_the_message_id_form_of_id()
    {
        var email = MailReadStubs.Parse(MailReadStubs.Email("m1", mailbox: "d", keywords: """{"$draft":true}"""));

        var message = EmailMapper.ToMessage(email);

        message.IsDraft.ShouldBeTrue();
        message.Id.ShouldBe(MailItemId.ForDraft("m1@example.com").ToMessageId());
    }

    [Theory]
    [InlineData("high", null, Importance.High)]
    [InlineData("Low", null, Importance.Low)]
    [InlineData(null, "1 (Highest)", Importance.High)]
    [InlineData(null, "5", Importance.Low)]
    [InlineData(null, "3", Importance.Normal)]
    [InlineData(null, null, Importance.Normal)]
    public void Importance_comes_from_the_importance_or_priority_header(string? importance, string? priority, Importance expected)
    {
        var json = MailReadStubs.Email("m1")
            .Replace("\"header:Importance:asText\":null", $"\"header:Importance:asText\":{(importance is null ? "null" : $"\"{importance}\"")}", StringComparison.Ordinal)
            .Replace("\"header:X-Priority:asText\":null", $"\"header:X-Priority:asText\":{(priority is null ? "null" : $"\"{priority}\"")}", StringComparison.Ordinal);

        EmailMapper.ToMessage(MailReadStubs.Parse(json)).Importance.ShouldBe(expected);
    }

    [Fact]
    public void Headers_are_unfolded_and_trimmed()
    {
        var email = MailReadStubs.Parse(MailReadStubs.Email("m1")) with
        {
            Headers = [new("Content-Type", " text/plain;\r\n\tcharset=utf-8"), new("Subject", " Hi")],
        };

        EmailMapper.ToMessage(email).InternetMessageHeaders.ShouldBe([new InternetHeader("Content-Type", "text/plain;\tcharset=utf-8"), new InternetHeader("Subject", "Hi")]);
    }

    [Fact]
    public void Sent_time_falls_back_to_the_received_time()
    {
        var email = MailReadStubs.Parse(MailReadStubs.Email("m1")) with { SentAt = null };

        EmailMapper.ToMessage(email).SentDateTime.ShouldBe(email.ReceivedAt);
    }

    [Theory]
    [InlineData("b", "b")]
    [InlineData("z", "a")]
    [InlineData(null, "a")]
    public void The_listed_folder_wins_as_parent_otherwise_the_smallest_id(string? folder, string expected)
    {
        var ids = new Dictionary<string, bool> { ["b"] = true, ["a"] = true, ["0"] = false };

        EmailMapper.ParentFolderOf(ids, folder).ShouldBe(expected);
        EmailMapper.ParentFolderOf(null, folder).ShouldBeNull();
    }
}
