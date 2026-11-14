using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail;

public sealed class MessageDtoMapperTests
{
    private static readonly DateTimeOffset Received = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private static MailMessage Message() => new(new MessageId("e.M1"))
    {
        Subject = "Quarterly numbers",
        From = new EmailAddress("alex@example.com", "Alex Sender"),
        ToRecipients = [new EmailAddress("me@example.com", "Me Example")],
        CcRecipients = [new EmailAddress("cc@example.com")],
        BodyPreview = "Hi, the Q2 numbers are attached.",
        Body = new ItemBody(BodyType.Html, "<p>Hi</p>"),
        IsRead = false,
        Flag = new FollowUpFlag(FlagStatus.Flagged),
        ReceivedDateTime = Received,
        SentDateTime = Received.AddSeconds(-2),
        Importance = Importance.High,
        ConversationId = "T1",
        HasAttachments = true,
        ParentFolderId = new FolderId("F1"),
        InternetMessageId = "abc@example.com",
        InternetMessageHeaders = [new InternetHeader("Content-Type", "text/plain")],
        Attachments = [new MailAttachment(new AttachmentId("A1"), "x.pdf", "application/pdf", 3, AttachmentKind.File)],
    };

    [Fact]
    public void Maps_the_list_projection_fields_the_client_reads()
    {
        var json = GraphJson.ToObject(MessageDtoMapper.ToDto(Message()));

        json["id"]!.GetValue<string>().ShouldBe("e.M1");
        json["subject"]!.GetValue<string>().ShouldBe("Quarterly numbers");
        Str(json, "from", "emailAddress", "address").ShouldBe("alex@example.com");
        Str(json, "from", "emailAddress", "name").ShouldBe("Alex Sender");
        Str(json, "sender", "emailAddress", "address").ShouldBe("alex@example.com");
        json["toRecipients"]![0]!["emailAddress"]!["address"]!.GetValue<string>().ShouldBe("me@example.com");
        json["ccRecipients"]![0]!["emailAddress"]!["name"]!.GetValue<string>().ShouldBe("cc@example.com");
        json["bccRecipients"]!.AsArray().ShouldBeEmpty();
        json["bodyPreview"]!.GetValue<string>().ShouldBe("Hi, the Q2 numbers are attached.");
        json["isRead"]!.GetValue<bool>().ShouldBeFalse();
        Str(json, "flag", "flagStatus").ShouldBe("flagged");
        json["receivedDateTime"]!.GetValue<string>().ShouldBe("2026-06-01T10:00:00Z");
        json["sentDateTime"]!.GetValue<string>().ShouldBe("2026-06-01T09:59:58Z");
        json["importance"]!.GetValue<string>().ShouldBe("high");
        json["conversationId"]!.GetValue<string>().ShouldBe("T1");
        json["hasAttachments"]!.GetValue<bool>().ShouldBeTrue();
        json["parentFolderId"]!.GetValue<string>().ShouldBe("F1");
        json["internetMessageId"]!.GetValue<string>().ShouldBe("<abc@example.com>");
        Str(json, "body", "contentType").ShouldBe("html");
        json.ContainsKey("@odata.type").ShouldBeFalse();
    }

    [Fact]
    public void Headers_and_attachments_appear_only_when_asked_for()
    {
        var plain = GraphJson.ToObject(MessageDtoMapper.ToDto(Message()));
        plain.ContainsKey("internetMessageHeaders").ShouldBeFalse();
        plain.ContainsKey("attachments").ShouldBeFalse();
        plain.ContainsKey("uniqueBody").ShouldBeFalse();
        plain.ContainsKey("singleValueExtendedProperties").ShouldBeFalse();

        var full = GraphJson.ToObject(MessageDtoMapper.ToDto(Message(), new MessageMappingOptions(
            Select: new HashSet<string>(StringComparer.Ordinal) { "InternetMessageHeaders", "body" },
            IncludeAttachments: true)));
        full["internetMessageHeaders"]![0]!["name"]!.GetValue<string>().ShouldBe("Content-Type");
        full["attachments"]![0]!["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.fileAttachment");
    }

    [Theory]
    [InlineData(FlagStatus.NotFlagged, "notFlagged")]
    [InlineData(FlagStatus.Complete, "complete")]
    public void Flag_status_spelling(FlagStatus status, string expected)
    {
        MessageDtoMapper.ToGraph(status).ShouldBe(expected);
    }

    [Theory]
    [InlineData(Importance.Low, "low")]
    [InlineData(Importance.Normal, "normal")]
    public void Importance_spelling(Importance importance, string expected)
    {
        MessageDtoMapper.ToGraph(importance).ShouldBe(expected);
    }

    [Fact]
    public void Missing_sender_and_body_are_null()
    {
        var json = GraphJson.ToObject(MessageDtoMapper.ToDto(new MailMessage(new MessageId("x"))));

        json["from"].ShouldBeNull();
        json["sender"].ShouldBeNull();
        json["body"].ShouldBeNull();
        json["internetMessageId"].ShouldBeNull();
        json.ContainsKey("receivedDateTime").ShouldBeTrue();
    }

    [Fact]
    public void Sender_takes_precedence_over_from_for_the_sender_field()
    {
        var dto = MessageDtoMapper.ToDto(Message() with { Sender = new EmailAddress("delegate@example.com") });

        dto.Sender!.EmailAddress!.Address.ShouldBe("delegate@example.com");
        dto.From!.EmailAddress!.Address.ShouldBe("alex@example.com");
    }

    [Fact]
    public void Invitation_is_an_event_message_request_with_meeting_fields_in_the_preferred_zone()
    {
        var message = Message() with { CalendarPart = new CalendarPart("REQUEST", new AttachmentId("P2")) };
        var meeting = new MeetingMessageDetails
        {
            Start = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
            Location = "Room 1",
        };

        var json = GraphJson.ToObject(MessageDtoMapper.ToDto(message, new MessageMappingOptions(Prefer: PreferHeader.Parse(["outlook.timezone=\"UTC\""]), Meeting: meeting)));

        json.First().Key.ShouldBe("@odata.type");
        json["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.eventMessageRequest");
        json["meetingMessageType"]!.GetValue<string>().ShouldBe("meetingRequest");
        Str(json, "startDateTime", "dateTime").ShouldBe("2026-07-01T09:00:00.0000000");
        Str(json, "startDateTime", "timeZone").ShouldBe("UTC");
        Str(json, "endDateTime", "dateTime").ShouldBe("2026-07-01T10:00:00.0000000");
        Str(json, "location", "displayName").ShouldBe("Room 1");
        json["isAllDay"]!.GetValue<bool>().ShouldBeFalse();
        json.ContainsKey("responseType").ShouldBeFalse();
        json["subject"]!.GetValue<string>().ShouldBe("Quarterly numbers");
    }

    [Fact]
    public void Meeting_times_follow_a_non_utc_preference()
    {
        var message = Message() with { CalendarPart = new CalendarPart("REQUEST", new AttachmentId("P2")) };
        var meeting = new MeetingMessageDetails { Start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero) };

        var dto = (EventMessageDto)MessageDtoMapper.ToDto(message, new MessageMappingOptions(Prefer: PreferHeader.Parse(["outlook.timezone=\"Australia/Sydney\""]), Meeting: meeting));

        dto.StartDateTime!.DateTime.ShouldBe(new DateTime(2026, 7, 1, 10, 0, 0));
        dto.StartDateTime.TimeZone.ShouldBe("Australia/Sydney");
        dto.EndDateTime.ShouldBeNull();
        dto.Location.ShouldBeNull();
    }

    [Fact]
    public void Cancellation_is_a_base_event_message()
    {
        var dto = (EventMessageDto)MessageDtoMapper.ToDto(Message() with { CalendarPart = new CalendarPart("CANCEL", new AttachmentId("P2")) });

        dto.ODataType.ShouldBe(EventMessageDto.EventMessageODataType);
        dto.MeetingMessageType.ShouldBe("meetingCancelled");
    }

    [Fact]
    public void Reply_with_unknown_partstat_stays_a_plain_message()
    {
        var dto = MessageDtoMapper.ToDto(Message() with { CalendarPart = new CalendarPart("REPLY", new AttachmentId("P2")) });

        dto.ShouldNotBeOfType<EventMessageDto>();
    }

    [Fact]
    public void Reply_is_an_event_message_response()
    {
        var reply = new CalendarPart("REPLY", new AttachmentId("P2")) { ReplyPartStat = "ACCEPTED" };
        var dto = (EventMessageDto)MessageDtoMapper.ToDto(Message() with { CalendarPart = reply });

        dto.ODataType.ShouldBe(EventMessageDto.ResponseODataType);
        dto.MeetingMessageType.ShouldBe("meetingAccepted");
        dto.ResponseType.ShouldBe("accepted");
        dto.ProposedNewTime.ShouldBeNull();
    }

    [Fact]
    public void Details_can_refine_a_reply_into_a_counter_proposal()
    {
        var meeting = new MeetingMessageDetails
        {
            MessageType = MeetingMessageType.MeetingDeclined,
            ProposedStart = new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero),
            ProposedEnd = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero),
            IsAllDay = true,
        };

        var json = GraphJson.ToObject(MessageDtoMapper.ToDto(Message() with { CalendarPart = new CalendarPart("REPLY", new AttachmentId("P2")) }, new MessageMappingOptions(Meeting: meeting)));

        json["meetingMessageType"]!.GetValue<string>().ShouldBe("meetingDeclined");
        json["responseType"]!.GetValue<string>().ShouldBe("declined");
        json["isAllDay"]!.GetValue<bool>().ShouldBeTrue();
        Str(json, "proposedNewTime", "start", "dateTime").ShouldBe("2026-07-02T09:00:00.0000000");
        Str(json, "proposedNewTime", "end", "timeZone").ShouldBe("UTC");
    }

    [Fact]
    public void Tentative_reply_spelling()
    {
        var dto = (EventMessageDto)MessageDtoMapper.ToDto(
            Message() with { CalendarPart = new CalendarPart("REPLY", new AttachmentId("P")) },
            new MessageMappingOptions(Meeting: new MeetingMessageDetails { MessageType = MeetingMessageType.MeetingTentativelyAccepted }));

        dto.MeetingMessageType.ShouldBe("meetingTentativelyAccepted");
        dto.ResponseType.ShouldBe("tentativelyAccepted");
    }

    [Fact]
    public void Unrecognised_calendar_methods_stay_plain_messages()
    {
        MessageDtoMapper.ToDto(Message() with { CalendarPart = new CalendarPart("PUBLISH", new AttachmentId("P")) })
            .ShouldNotBeOfType<EventMessageDto>();
        MessageDtoMapper.ToGraph(MeetingMessageType.None).ShouldBe("none");
    }

    [Fact]
    public void File_and_item_attachments()
    {
        var file = MessageDtoMapper.ToDto(new MailAttachment(new AttachmentId("A"), "logo.png", "image/png", 10, AttachmentKind.File)
        {
            IsInline = true,
            ContentId = "logo@1",
            ContentBytes = Encoding.UTF8.GetBytes("hi"),
            LastModifiedDateTime = Received,
        });
        var item = MessageDtoMapper.ToDto(new MailAttachment(new AttachmentId("B"), "Fwd: hello", string.Empty, 99, AttachmentKind.Item) { ContentId = "ignored" });

        file.ODataType.ShouldBe(AttachmentDto.FileAttachmentType);
        file.ContentBytes.ShouldBe("aGk=");
        file.ContentId.ShouldBe("logo@1");
        file.IsInline.ShouldBe(true);
        item.ODataType.ShouldBe(AttachmentDto.ItemAttachmentType);
        item.ContentType.ShouldBe("message/rfc822");
        item.ContentId.ShouldBeNull();
        item.ContentBytes.ShouldBeNull();
        GraphJson.ToObject(item).ContainsKey("contentBytes").ShouldBeFalse();
    }

    [Fact]
    public void Folder_has_exactly_the_selected_properties()
    {
        var json = GraphJson.ToObject(MessageDtoMapper.ToDto(new MailFolder(new FolderId("F1"), "Inbox", new FolderId("R"), 1, 3, 120) { IsHidden = true }));

        var expected = JsonNode.Parse("""{"id":"F1","displayName":"Inbox","parentFolderId":"R","childFolderCount":1,"unreadItemCount":3,"totalItemCount":120,"isHidden":true}""");
        JsonNode.DeepEquals(json, expected).ShouldBeTrue(json.ToJsonString());
    }

    [Fact]
    public void Draft_input_binds_case_insensitively_with_extended_properties()
    {
        const string body = """
            {"subject":"s","body":{"contentType":"Html","content":"<p/>"},
             "ToRecipients":[{"emailAddress":{"address":"a@example.com"}}],
             "attachments":[{"@odata.type":"#microsoft.graph.fileAttachment","name":"x","contentType":"text/plain","contentBytes":"aGk=","isInline":true,"contentId":"c"}],
             "singleValueExtendedProperties":[{"id":"SystemTime 0x0E06","value":"2026-01-01T00:00:00.000Z"},{"id":"Integer 0x0E07","value":"1"}],
             "isRead":true}
            """;

        var dto = JsonSerializer.Deserialize<MessageDto>(body, GraphJson.Options)!;

        dto.Body!.ContentType.ShouldBe("Html");
        dto.ToRecipients!.Single().EmailAddress!.Address.ShouldBe("a@example.com");
        dto.Attachments!.Single().ContentBytes.ShouldBe("aGk=");
        dto.SingleValueExtendedProperties!.Select(p => p.Id).ShouldBe(["SystemTime 0x0E06", "Integer 0x0E07"]);
        dto.IsRead.ShouldBe(true);
    }

    [Fact]
    public void Item_body_and_recipient_helpers()
    {
        MessageDtoMapper.ToDto((ItemBody?)null).ShouldBeNull();
        MessageDtoMapper.ToDto(new ItemBody(BodyType.Text, "t"))!.ContentType.ShouldBe("text");
        MessageDtoMapper.ToRecipientDto(new EmailAddress("x@example.com", "X")).EmailAddress!.Name.ShouldBe("X");
    }

    private static string? Str(JsonNode node, params string[] path)
    {
        JsonNode? current = node;
        foreach (var segment in path)
        {
            current = current?[segment];
        }

        return current?.GetValue<string>();
    }
}
