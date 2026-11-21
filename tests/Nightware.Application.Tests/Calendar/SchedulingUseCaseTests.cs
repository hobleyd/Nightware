using Nightware.Application.Calendar;
using Nightware.Application.Calendar.Scheduling;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Calendar;

public sealed class SchedulingUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EventId Id = new("e1");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly ISchedulingActions _scheduling = Substitute.For<ISchedulingActions>();

    private static TimeSlot Slot(int startHour, int endHour) =>
        new(new DateTimeTimeZone(new DateTime(2026, 6, 10, startHour, 0, 0), "UTC"), new DateTimeTimeZone(new DateTime(2026, 6, 10, endHour, 0, 0), "UTC"));

    [Fact]
    public async Task An_rsvp_passes_its_comment_and_proposal_through()
    {
        _scheduling.RespondAsync(Me, Id, RsvpResponse.Decline, "busy", true, Slot(11, 12), Ct).Returns(Result<ItipMessage?>.Success(null));

        (await new RespondToEvent(_scheduling, Substitute.For<IItipMail>()).ExecuteAsync(Me, Id, new RsvpRequest(RsvpResponse.Decline, "busy", true, Slot(11, 12)), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_empty_comment_is_none()
    {
        _scheduling.RespondAsync(Me, Id, RsvpResponse.Accept, null, true, null, Ct).Returns(Result<ItipMessage?>.Success(null));
        (await new RespondToEvent(_scheduling, Substitute.For<IItipMail>()).ExecuteAsync(Me, Id, new RsvpRequest(RsvpResponse.Accept, string.Empty, true), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(RsvpResponse.Accept, 11, 12)]      // proposals only go with decline/tentative
    [InlineData(RsvpResponse.Decline, 12, 11)]     // ordered
    [InlineData(RsvpResponse.TentativelyAccept, 11, 11)] // and not empty
    public async Task Invalid_proposals_are_rejected(RsvpResponse response, int start, int end)
    {
        (await new RespondToEvent(_scheduling, Substitute.For<IItipMail>()).ExecuteAsync(Me, Id, new RsvpRequest(response, null, true, Slot(start, end)), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _scheduling.DidNotReceiveWithAnyArgs().RespondAsync(default, default, default, default, default, default, Ct);
    }

    [Fact]
    public async Task Cancel_passes_through_with_an_empty_comment_as_none()
    {
        _scheduling.CancelAsync(Me, Id, null, Ct).Returns(Result.Success());
        (await new CancelEvent(_scheduling).ExecuteAsync(Me, Id, string.Empty, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Forward_needs_valid_recipients()
    {
        var forward = new ForwardEvent(_scheduling);
        _scheduling.ForwardAsync(Me, Id, Arg.Any<IReadOnlyList<EmailAddress>>(), "hi", Ct).Returns(Result.Success());

        (await forward.ExecuteAsync(Me, Id, [new EmailAddress("pat@example.com")], "hi", Ct)).IsSuccess.ShouldBeTrue();
        (await forward.ExecuteAsync(Me, Id, [], "hi", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await forward.ExecuteAsync(Me, Id, [new EmailAddress("pat")], "hi", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _scheduling.Received(1).ForwardAsync(Me, Id, Arg.Any<IReadOnlyList<EmailAddress>>(), "hi", Ct);
    }

    [Fact]
    public async Task A_reply_the_server_cannot_send_is_mailed_from_the_mailbox()
    {
        var mail = new ItipMessage("dana@contoso.example", "Declined: Review", "Away", "BEGIN:VCALENDAR", "REPLY");
        _scheduling.RespondAsync(Me, Id, RsvpResponse.Decline, "Away", true, null, Ct).Returns(Result<ItipMessage?>.Success(mail));
        var itip = Substitute.For<IItipMail>();
        itip.SendAsync(Me, mail, Ct).Returns(Result.Success());

        (await new RespondToEvent(_scheduling, itip).ExecuteAsync(Me, Id, new RsvpRequest(RsvpResponse.Decline, "Away", true), Ct)).IsSuccess.ShouldBeTrue();

        await itip.Received(1).SendAsync(Me, mail, Ct);
    }

    [Fact]
    public async Task A_reply_that_cannot_be_mailed_fails_the_answer()
    {
        var mail = new ItipMessage("dana@contoso.example", "Declined: Review", "Away", "BEGIN:VCALENDAR", "REPLY");
        _scheduling.RespondAsync(Me, Id, RsvpResponse.Decline, "Away", true, null, Ct).Returns(Result<ItipMessage?>.Success(mail));
        var itip = Substitute.For<IItipMail>();
        itip.SendAsync(Me, mail, Ct).Returns(Error.Forbidden("send-as"));

        (await new RespondToEvent(_scheduling, itip).ExecuteAsync(Me, Id, new RsvpRequest(RsvpResponse.Decline, "Away", true), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}
