using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Domain.Tests.Mail;

public sealed class CalendarPartTests
{
    [Theory]
    [InlineData("REQUEST", MeetingMessageType.MeetingRequest, true)]
    [InlineData("request", MeetingMessageType.MeetingRequest, true)]
    [InlineData("CANCEL", MeetingMessageType.MeetingCancelled, true)]
    [InlineData("REPLY", MeetingMessageType.None, false)]
    [InlineData("PUBLISH", MeetingMessageType.None, true)]
    [InlineData("COUNTER", MeetingMessageType.None, true)]
    public void Maps_itip_methods(string method, MeetingMessageType type, bool isRequest)
    {
        var part = new CalendarPart(method, new AttachmentId("2"));

        part.MessageType.ShouldBe(type);
        part.IsRequest.ShouldBe(isRequest);
    }

    [Theory]
    [InlineData("ACCEPTED", MeetingMessageType.MeetingAccepted)]
    [InlineData("tentative", MeetingMessageType.MeetingTentativelyAccepted)]
    [InlineData("DECLINED", MeetingMessageType.MeetingDeclined)]
    [InlineData("NEEDS-ACTION", MeetingMessageType.None)]
    public void Reply_type_follows_the_attendee_partstat(string partStat, MeetingMessageType type)
    {
        var part = new CalendarPart("Reply", new AttachmentId("2")) { ReplyPartStat = partStat };

        part.MessageType.ShouldBe(type);
    }

    [Fact]
    public void Message_defaults_are_graph_defaults()
    {
        var message = new MailMessage(new MessageId("e.1"));

        message.Flag.ShouldBe(FollowUpFlag.NotFlagged);
        message.Importance.ShouldBe(Importance.Normal);
        message.ToRecipients.ShouldBeEmpty();
        message.Attachments.ShouldBeEmpty();
        message.CalendarPart.ShouldBeNull();
    }

    [Fact]
    public void Write_models_default_to_empty()
    {
        new MessagePatch().IsRead.ShouldBeNull();
        new DraftContent().ToRecipients.ShouldBeEmpty();
        new DraftContent().Importance.ShouldBe(Importance.Normal);
        new NewAttachment("a", "text/plain", new byte[] { 1 }).IsInline.ShouldBeFalse();
        new MailFolder(new FolderId("f"), "Inbox", null, 0, 0, 0).WellKnownName.ShouldBeNull();
        new FolderRef.WellKnown(WellKnownFolder.Inbox).ShouldBe(new FolderRef.WellKnown(WellKnownFolder.Inbox));
    }
}
