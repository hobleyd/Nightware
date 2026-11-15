using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail.Read;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class MimePartsTests
{
    private static EmailBodyPart Leaf(string partId, string type, string? contentType = null, string? name = null) => new()
    {
        PartId = partId,
        BlobId = "B" + partId,
        Type = type,
        Name = name,
        Size = 10,
        Extra = contentType is null ? null : new() { [EmailProperties.PartContentTypeProperty] = System.Text.Json.JsonSerializer.SerializeToElement(contentType) },
    };

    private static EmailBodyPart Multipart(string type, params EmailBodyPart[] parts) => new() { Type = type, SubParts = parts };

    [Theory]
    [InlineData(" text/calendar; method=REQUEST; charset=\"utf-8\"", "method", "REQUEST")]
    [InlineData("text/calendar; charset=utf-8; METHOD=\"reply\"", "method", "reply")]
    [InlineData("text/calendar; charset=utf-8", "method", null)]
    [InlineData(null, "method", null)]
    [InlineData("text/calendar; method", "method", null)]
    public void Reads_content_type_parameters(string? header, string name, string? expected)
    {
        MimeParts.Parameter(header, name).ShouldBe(expected);
    }

    [Fact]
    public void Finds_the_itip_part_anywhere_in_the_tree()
    {
        var root = Multipart("multipart/mixed",
            Multipart("multipart/alternative", Leaf("1", "text/plain"), Leaf("2", "text/calendar", " text/calendar; method=reply")),
            Leaf("3", "application/pdf"));

        MimeParts.CalendarPartOf(root).ShouldBe(new CalendarPart("REPLY", AttachmentItemId.Assign(root)["2"].ToAttachmentId()));
        MimeParts.Find(root, "3")!.Type.ShouldBe("application/pdf");
        MimeParts.Find(root, "9").ShouldBeNull();
    }

    [Fact]
    public void A_calendar_file_without_a_method_is_not_an_invitation()
    {
        MimeParts.CalendarPartOf(Multipart("multipart/mixed", Leaf("1", "text/calendar", "text/calendar; name=event.ics"))).ShouldBeNull();
        MimeParts.CalendarPartOf(null).ShouldBeNull();
    }

    [Fact]
    public void Attachments_include_an_itip_part_jmap_left_in_the_alternative()
    {
        var calendar = Leaf("2", "text/calendar", "text/calendar; method=REQUEST");
        var email = new Email
        {
            Id = "m",
            BodyStructure = Multipart("multipart/mixed", Multipart("multipart/alternative", Leaf("1", "text/html"), calendar), Leaf("3", "message/rfc822")),
            Attachments = [Leaf("3", "message/rfc822")],
        };

        var attachments = MimeParts.Attachments(email);

        var ids = AttachmentItemId.Assign(email.BodyStructure);
        attachments.Select(a => (a.Id, a.Name, a.Kind)).ShouldBe(
        [
            (ids["3"].ToAttachmentId(), "Attached message", AttachmentKind.Item),
            (ids["2"].ToAttachmentId(), "invite.ics", AttachmentKind.File),
        ]);
    }

    [Theory]
    [InlineData("inline", "<img1@x>", true)]
    [InlineData(null, "img1@x", true)]
    [InlineData("attachment", "img1@x", false)]
    [InlineData("inline", null, false)]
    public void Inline_means_referenced_by_content_id(string? disposition, string? cid, bool inline)
    {
        var attachment = MimeParts.ToAttachment(Leaf("1", "image/PNG") with { Disposition = disposition, Cid = cid }, new AttachmentId("a.x"), null);

        attachment.IsInline.ShouldBe(inline);
        attachment.ContentType.ShouldBe("image/png");
        if (cid is not null)
        {
            attachment.ContentId.ShouldBe("img1@x");
        }
    }

    [Fact]
    public void A_part_without_a_type_or_name_gets_defaults()
    {
        var attachment = MimeParts.ToAttachment(new EmailBodyPart { PartId = "7" }, new AttachmentId("a.7"), DateTimeOffset.UnixEpoch);

        attachment.ShouldBe(new MailAttachment(new AttachmentId("a.7"), "attachment", "application/octet-stream", 0, AttachmentKind.File)
        {
            LastModifiedDateTime = DateTimeOffset.UnixEpoch,
        });
    }
}
