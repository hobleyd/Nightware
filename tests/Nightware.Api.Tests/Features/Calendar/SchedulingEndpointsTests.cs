using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Tests.Support;
using Nightware.Application.Calendar;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Calendar;

/// <summary>RSVP, cancel, forward, invitation mail → event, and getSchedule.</summary>
public sealed class SchedulingEndpointsTests : IAsyncLifetime
{
    private static readonly EventId Id = new("ev-1");
    private static readonly MessageId Message = new("msg-1");
    private readonly ISchedulingActions _scheduling = Substitute.For<ISchedulingActions>();
    private readonly ICalendarReader _reader = Substitute.For<ICalendarReader>();
    private readonly IMailMessageReader _mail = Substitute.For<IMailMessageReader>();
    private readonly IFreeBusyReader _freeBusy = Substitute.For<IFreeBusyReader>();
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services.AddSingleton(_scheduling).AddSingleton(_reader).AddSingleton(_mail).AddSingleton(_freeBusy));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Theory]
    [InlineData("/v1.0/me", "me@example.com", "accept", RsvpResponse.Accept)]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com", "tentativelyAccept", RsvpResponse.TentativelyAccept)]
    [InlineData("/v1.0/me", "me@example.com", "decline", RsvpResponse.Decline)]
    public async Task Event_rsvps_answer_202(string prefix, string mailbox, string action, RsvpResponse response)
    {
        _scheduling.RespondAsync(new MailboxRef(mailbox), Id, response, "ok", true, null, Arg.Any<CancellationToken>()).Returns(Result<ItipMessage?>.Success(null));

        var result = await _host.AuthorizedClient().PostAsync($"{prefix}/events/ev-1/{action}", Json("""{"sendResponse":true,"comment":"ok"}"""), Ct);

        result.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_decline_carries_the_proposed_time()
    {
        _scheduling.RespondAsync(new MailboxRef(TestHost.UserEmail), Id, RsvpResponse.Decline, null, true,
                new TimeSlot(new DateTimeTimeZone(new DateTime(2026, 6, 10, 11, 30, 0), "UTC"), new DateTimeTimeZone(new DateTime(2026, 6, 10, 12, 30, 0), "UTC")), Arg.Any<CancellationToken>())
            .Returns(Result<ItipMessage?>.Success(null));

        var result = await _host.AuthorizedClient().PostAsync("/v1.0/me/events/ev-1/decline",
            Json("""{"sendResponse":true,"comment":"","proposedNewTime":{"start":{"dateTime":"2026-06-10T11:30:00.000","timeZone":"UTC"},"end":{"dateTime":"2026-06-10T12:30:00.000","timeZone":"UTC"}}}"""), Ct);

        result.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Proposals_must_be_well_formed_and_errors_are_mapped()
    {
        (await _host.AuthorizedClient().PostAsync("/v1.0/me/events/ev-1/accept",
            Json("""{"proposedNewTime":{"start":{"dateTime":"2026-06-10T11:30:00","timeZone":"UTC"},"end":{"dateTime":"2026-06-10T12:30:00","timeZone":"UTC"}}}"""), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _host.AuthorizedClient().PostAsync("/v1.0/me/events/ev-1/decline", Json("""{"proposedNewTime":{"start":{"dateTime":"2026-06-10T11:30:00","timeZone":"UTC"}}}"""), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        _scheduling.RespondAsync(Arg.Any<MailboxRef>(), Id, RsvpResponse.Accept, null, true, null, Arg.Any<CancellationToken>()).Returns(Result<ItipMessage?>.Failure(Error.NotFound("gone")));
        (await _host.AuthorizedClient().PostAsync("/v1.0/me/events/ev-1/accept", Json("{}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/v1.0/me", "me@example.com")]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com")]
    public async Task Cancel_and_forward_answer_202(string prefix, string mailbox)
    {
        _scheduling.CancelAsync(new MailboxRef(mailbox), Id, null, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _scheduling.ForwardAsync(new MailboxRef(mailbox), Id, Arg.Is<IReadOnlyList<EmailAddress>>(r => r.Single().Address == "pat@example.com"), "Join", Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        (await _host.AuthorizedClient().PostAsync($"{prefix}/events/ev-1/cancel", Json("""{"comment":""}"""), Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await _host.AuthorizedClient().PostAsync($"{prefix}/events/ev-1/forward", Json("""{"ToRecipients":[{"emailAddress":{"address":"pat@example.com"}}],"Comment":"Join"}"""), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task An_attendee_forward_is_403_so_the_client_mails_the_invite()
    {
        _scheduling.ForwardAsync(Arg.Any<MailboxRef>(), Id, Arg.Any<IReadOnlyList<EmailAddress>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Error.Forbidden("attendee"));

        (await _host.AuthorizedClient().PostAsync("/v1.0/me/events/ev-1/forward", Json("""{"ToRecipients":[{"emailAddress":{"address":"pat@example.com"}}]}"""), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _host.AuthorizedClient().PostAsync("/v1.0/me/events/ev-1/forward", Json("""{"ToRecipients":[{"emailAddress":{}}]}"""), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private void ArrangeInvitation(string mailbox, bool isOrganizer = false)
    {
        var owner = new MailboxRef(mailbox);
        var part = new AttachmentId("2");
        _mail.GetAsync(owner, Message, Arg.Any<MessageFetch>(), Arg.Any<CancellationToken>()).Returns(new MailMessage(Message) { CalendarPart = new CalendarPart("REQUEST", part) });
        var ics = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:uid-1\r\nDTSTAMP:20260601T000000Z\r\nDTSTART:20260610T090000Z\r\nDTEND:20260610T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        _mail.GetAttachmentContentAsync(owner, Message, part, Arg.Any<CancellationToken>()).Returns(new BlobContent("text/calendar", ics.Length, _ => Task.FromResult<Stream>(new MemoryStream(ics))));
        _reader.ListAsync(owner, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<CalendarEvent>([EventEndpointsTests.Review() with { IsOrganizer = isOrganizer }]));
    }

    [Theory]
    [InlineData("/v1.0/me", "me@example.com", "/microsoft.graph.eventMessage")]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com", "/microsoft.graph.eventMessageRequest")]
    [InlineData("/v1.0/me", "me@example.com", "/microsoft.graph.eventMessageResponse")]
    [InlineData("/v1.0/users/shared%40example.com", "shared@example.com", "")]
    public async Task Every_cast_spelling_resolves_the_linked_event(string prefix, string mailbox, string cast)
    {
        ArrangeInvitation(mailbox);

        var response = await _host.AuthorizedClient().GetAsync($"{prefix}/messages/msg-1{cast}/event?$select=id,isOrganizer", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.Str("id").ShouldBe("ev-1");
        body["isOrganizer"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Theory]
    [InlineData("/v1.0/me/messages/msg-1/microsoft.graph.eventMessageFoo/event")]
    [InlineData("/v1.0/me/messages/msg-1/microsoft.graph.eventMessage/somethingElse")]
    public async Task Other_spellings_are_graphs_unknown_segment_400(string path) =>
        (await _host.AuthorizedClient().GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task A_message_that_is_not_an_invitation_is_404()
    {
        _mail.GetAsync(Arg.Any<MailboxRef>(), Message, Arg.Any<MessageFetch>(), Arg.Any<CancellationToken>()).Returns(new MailMessage(Message));
        (await _host.AuthorizedClient().GetAsync("/v1.0/me/messages/msg-1/event", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _host.AuthorizedClient().PostAsync("/v1.0/me/messages/msg-1/accept", Json("{}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/v1.0/me", "me@example.com", "accept", RsvpResponse.Accept)]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com", "tentativelyAccept", RsvpResponse.TentativelyAccept)]
    [InlineData("/v1.0/me", "me@example.com", "decline", RsvpResponse.Decline)]
    public async Task Message_rsvps_answer_the_linked_event(string prefix, string mailbox, string action, RsvpResponse response)
    {
        ArrangeInvitation(mailbox);
        _scheduling.RespondAsync(new MailboxRef(mailbox), Id, response, null, true, null, Arg.Any<CancellationToken>()).Returns(Result<ItipMessage?>.Success(null));

        (await _host.AuthorizedClient().PostAsync($"{prefix}/messages/msg-1/{action}", Json("""{"sendResponse":true,"comment":""}"""), Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task The_organizer_responding_from_the_mail_gets_400()
    {
        ArrangeInvitation(TestHost.UserEmail, isOrganizer: true);
        (await _host.AuthorizedClient().PostAsync("/v1.0/me/messages/msg-1/accept", Json("{}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/v1.0/me", "me@example.com")]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com")]
    public async Task Get_schedule_returns_availability_in_the_request_zone(string prefix, string mailbox)
    {
        var start = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        _freeBusy.GetAsync(new MailboxRef(mailbox), Arg.Any<IReadOnlyList<string>>(), start, start.AddHours(2), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<FreeBusySchedule>>.Success(
            [
                new FreeBusySchedule("sam@example.com", [new ScheduleItem(start.AddMinutes(30), start.AddMinutes(60), FreeBusyStatus.Tentative)]),
                new FreeBusySchedule("x@example.com", []) { ErrorMessage = "unknown" },
            ]));

        var response = await _host.AuthorizedClient().PostAsync($"{prefix}/calendar/getSchedule",
            Json("""{"schedules":["sam@example.com","x@example.com"],"startTime":{"dateTime":"2026-06-10T00:00:00.000","timeZone":"UTC"},"endTime":{"dateTime":"2026-06-10T02:00:00.000","timeZone":"UTC"}}"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var value = (await response.ReadObjectAsync())["value"]!.AsArray();
        value[0].Str("availabilityView").ShouldBe("0100");
        value[0]!["scheduleItems"]![0].Str("status").ShouldBe("tentative");
        value[0]!["scheduleItems"]![0].Str("start.dateTime").ShouldBe("2026-06-10T00:30:00.0000000");
        value[0]!["scheduleItems"]![0]!.AsObject().ContainsKey("subject").ShouldBeFalse();
        value[0]!.AsObject().ContainsKey("error").ShouldBeFalse();
        value[1].Str("error.message").ShouldBe("unknown");
    }

    [Fact]
    public async Task Get_schedule_validates_its_body()
    {
        (await _host.AuthorizedClient().PostAsync("/v1.0/me/calendar/getSchedule", Json("""{"schedules":["a@example.com"]}"""), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _host.AuthorizedClient().PostAsync("/v1.0/me/calendar/getSchedule",
            Json("""{"schedules":[],"startTime":{"dateTime":"2026-06-10T00:00:00","timeZone":"UTC"},"endTime":{"dateTime":"2026-06-10T02:00:00","timeZone":"UTC"}}"""), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
