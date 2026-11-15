using System.Text.Json.Nodes;
using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Features.Mail.Write.Dto;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Write;

public sealed class MailWriteRequestMapperTests
{
    private static RecipientDto To(string? address, string? name = null) => new() { EmailAddress = new EmailAddressDto { Address = address, Name = name } };

    [Theory]
    [InlineData("Html", BodyType.Html)]
    [InlineData("html", BodyType.Html)]
    [InlineData("HTML", BodyType.Html)]
    [InlineData("Text", BodyType.Text)]
    [InlineData("text", BodyType.Text)]
    [InlineData(null, BodyType.Text)]
    [InlineData("", BodyType.Text)]
    public void Body_content_type_is_case_insensitive_and_defaults_to_text(string? contentType, BodyType expected)
    {
        var body = MailWriteRequestMapper.ToBody(new ItemBodyDto { ContentType = contentType, Content = "x" });

        body.Value.ShouldBe(new ItemBody(expected, "x"));
    }

    [Fact]
    public void An_unknown_content_type_is_a_validation_error()
    {
        var body = MailWriteRequestMapper.ToBody(new ItemBodyDto { ContentType = "rtf" });

        body.Error!.Kind.ShouldBe(ErrorKind.Validation);
        body.Error.Target.ShouldBe("body.contentType");
    }

    [Fact]
    public void No_body_maps_to_null()
    {
        MailWriteRequestMapper.ToBody(null).Value.ShouldBeNull();
    }

    [Fact]
    public void Content_maps_every_member()
    {
        var content = MailWriteRequestMapper.ToContent(new MessageDto
        {
            Subject = "S",
            Body = new ItemBodyDto { ContentType = "html", Content = "<p/>" },
            From = To("alex@example.com", "Alex"),
            ToRecipients = [To(" a@example.com ")],
            CcRecipients = [To("c@example.com", " ")],
            BccRecipients = [To("b@example.com")],
            ReplyTo = [To("r@example.com")],
            Importance = "High",
            Attachments = [new AttachmentDto { Name = "a.txt", ContentType = "text/plain", ContentBytes = "YQ==" }],
        });

        var c = content.Value;
        c.Subject.ShouldBe("S");
        c.Body.ShouldBe(new ItemBody(BodyType.Html, "<p/>"));
        c.From.ShouldBe(new EmailAddress("alex@example.com", "Alex"));
        c.ToRecipients.ShouldBe([new EmailAddress("a@example.com")]);
        c.CcRecipients.ShouldBe([new EmailAddress("c@example.com")]);
        c.BccRecipients.Single().Address.ShouldBe("b@example.com");
        c.ReplyTo.Single().Address.ShouldBe("r@example.com");
        c.Importance.ShouldBe(Importance.High);
        c.Attachments.Single().Content.ToArray().ShouldBe("a"u8.ToArray());
    }

    [Fact]
    public void Content_defaults_to_normal_importance_and_no_lists()
    {
        var c = MailWriteRequestMapper.ToContent(new MessageDto()).Value;

        c.Importance.ShouldBe(Importance.Normal);
        c.ToRecipients.ShouldBeEmpty();
        c.From.ShouldBeNull();
        c.Attachments.ShouldBeEmpty();
    }

    [Fact]
    public void Content_needs_a_message()
    {
        MailWriteRequestMapper.ToContent(null).Error!.Target.ShouldBe("message");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void A_recipient_without_an_address_is_a_validation_error(string? address)
    {
        var content = MailWriteRequestMapper.ToContent(new MessageDto { CcRecipients = [To(address)] });

        content.Error!.Kind.ShouldBe(ErrorKind.Validation);
        content.Error.Target.ShouldBe("ccRecipients");
    }

    [Fact]
    public void A_bad_importance_is_a_validation_error()
    {
        MailWriteRequestMapper.ToContent(new MessageDto { Importance = "urgent" }).Error!.Target.ShouldBe("importance");
    }

    [Fact]
    public void Composed_prefers_top_level_recipients_over_the_message()
    {
        var composed = MailWriteRequestMapper.ToComposed(new ReplyRequestDto
        {
            Message = new MessageDto { ToRecipients = [To("inner@example.com")], CcRecipients = [To("cc@example.com")] },
            ToRecipients = [To("top@example.com")],
        }).Value;

        composed.ToRecipients!.Single().Address.ShouldBe("top@example.com");
        composed.CcRecipients!.Single().Address.ShouldBe("cc@example.com");
    }

    [Fact]
    public void Composed_falls_back_to_message_recipients()
    {
        var composed = MailWriteRequestMapper.ToComposed(new ReplyRequestDto { Message = new MessageDto { ToRecipients = [To("inner@example.com")] }, ToRecipients = [] }).Value;

        composed.ToRecipients!.Single().Address.ShouldBe("inner@example.com");
    }

    [Fact]
    public void Composed_body_wins_over_comment_and_comment_alone_is_text()
    {
        MailWriteRequestMapper.ToComposed(new ReplyRequestDto
        {
            Message = new MessageDto { Body = new ItemBodyDto { ContentType = "html", Content = "<p>b</p>" } },
            Comment = "c",
        }).Value.Body.ShouldBe(new ItemBody(BodyType.Html, "<p>b</p>"));

        MailWriteRequestMapper.ToComposed(new ReplyRequestDto { Comment = "c" }).Value.Body.ShouldBe(new ItemBody(BodyType.Text, "c"));
    }

    [Fact]
    public void Composed_with_no_request_is_empty()
    {
        var composed = MailWriteRequestMapper.ToComposed(null).Value;

        composed.Body.ShouldBeNull();
        composed.Subject.ShouldBeNull();
        composed.ToRecipients.ShouldBeEmpty();
    }

    [Fact]
    public void Composed_rejects_a_bad_recipient()
    {
        MailWriteRequestMapper.ToComposed(new ReplyRequestDto { ToRecipients = [To(null)] }).Error!.Target.ShouldBe("toRecipients");
    }

    [Fact]
    public void Update_with_only_isRead_is_a_message_patch()
    {
        var update = MailWriteRequestMapper.ToUpdate(JsonNode.Parse("""{"isRead":true}""")!.AsObject()).Value;

        update.Draft.ShouldBeNull();
        update.Patch!.IsRead.ShouldBe(true);
        update.HasPatch.ShouldBeTrue();
    }

    [Theory]
    [InlineData("flagged", FlagStatus.Flagged)]
    [InlineData("notFlagged", FlagStatus.NotFlagged)]
    [InlineData("complete", FlagStatus.Complete)]
    public void Update_maps_the_flag(string status, FlagStatus expected)
    {
        var update = MailWriteRequestMapper.ToUpdate(JsonNode.Parse($$$"""{"flag":{"flagStatus":"{{{status}}}"}}""")!.AsObject()).Value;

        update.Patch!.Flag!.Status.ShouldBe(expected);
    }

    [Fact]
    public void Update_rejects_an_unknown_flag()
    {
        MailWriteRequestMapper.ToUpdate(JsonNode.Parse("""{"flag":{"flagStatus":"maybe"}}""")!.AsObject()).Error!.Target.ShouldBe("flag.flagStatus");
    }

    [Fact]
    public void Update_with_any_draft_field_is_a_draft_update_and_absent_fields_stay_null()
    {
        var update = MailWriteRequestMapper.ToUpdate(JsonNode.Parse("""{"Subject":"S","importance":"low"}""")!.AsObject()).Value;

        update.Patch.ShouldBeNull();
        var draft = update.Draft!;
        draft.Subject.ShouldBe("S");
        draft.Importance.ShouldBe(Importance.Low);
        draft.Body.ShouldBeNull();
        draft.ToRecipients.ShouldBeNull();
        draft.Missing().ShouldBe(["body", "toRecipients", "ccRecipients", "bccRecipients"], ignoreOrder: true);
    }

    [Fact]
    public void Update_explicit_nulls_clear_draft_fields()
    {
        var draft = MailWriteRequestMapper.ToUpdate(JsonNode.Parse("""{"subject":null,"body":null,"toRecipients":null,"ccRecipients":[],"bccRecipients":[]}""")!.AsObject()).Value.Draft!;

        draft.Subject.ShouldBe(string.Empty);
        draft.Body.ShouldBe(new ItemBody(BodyType.Text, string.Empty));
        draft.ToRecipients.ShouldBeEmpty();
        draft.CcRecipients.ShouldBeEmpty();
        draft.Missing().ShouldBe(["importance"]);
    }

    [Fact]
    public void Update_draft_with_a_bad_recipient_is_a_validation_error()
    {
        MailWriteRequestMapper.ToUpdate(JsonNode.Parse("""{"toRecipients":[{"emailAddress":{}}]}""")!.AsObject()).Error!.Target.ShouldBe("toRecipients");
    }

    [Theory]
    [InlineData("""{"isRead":"yes"}""")]
    [InlineData("""{"importance":"loud"}""")]
    public void Update_with_malformed_values_is_a_validation_error(string json)
    {
        MailWriteRequestMapper.ToUpdate(JsonNode.Parse(json)!.AsObject()).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Update_needs_a_body()
    {
        MailWriteRequestMapper.ToUpdate(null).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Attachment_maps_a_file_attachment()
    {
        var attachment = MailWriteRequestMapper.ToAttachment(new AttachmentDto
        {
            ODataType = "#microsoft.graph.FileAttachment",
            Name = "img.png",
            ContentType = "image/png",
            ContentBytes = "AQI=",
            IsInline = true,
            ContentId = "cid1",
        }).Value;

        attachment.Name.ShouldBe("img.png");
        attachment.ContentType.ShouldBe("image/png");
        attachment.Content.ToArray().ShouldBe(new byte[] { 1, 2 });
        attachment.IsInline.ShouldBeTrue();
        attachment.ContentId.ShouldBe("cid1");
    }

    [Fact]
    public void Attachment_defaults_type_and_inline()
    {
        var attachment = MailWriteRequestMapper.ToAttachment(new AttachmentDto { Name = "a" }).Value;

        attachment.ContentType.ShouldBe("application/octet-stream");
        attachment.Content.Length.ShouldBe(0);
        attachment.IsInline.ShouldBeFalse();
    }

    [Fact]
    public void Attachment_errors()
    {
        MailWriteRequestMapper.ToAttachment(null).Error!.Kind.ShouldBe(ErrorKind.Validation);
        MailWriteRequestMapper.ToAttachment(new AttachmentDto { ODataType = AttachmentDto.ItemAttachmentType, Name = "a" }).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        MailWriteRequestMapper.ToAttachment(new AttachmentDto { Name = " " }).Error!.Target.ShouldBe("name");
        MailWriteRequestMapper.ToAttachment(new AttachmentDto { Name = "a", ContentBytes = "!!" }).Error!.Target.ShouldBe("contentBytes");
    }
}
