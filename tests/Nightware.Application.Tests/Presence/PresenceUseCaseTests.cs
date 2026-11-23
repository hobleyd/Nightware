using Microsoft.Extensions.Time.Testing;

using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Application.MailboxSettings;
using Nightware.Application.Presence;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;
using Nightware.Domain.Presence;

namespace Nightware.Application.Tests.Presence;

public sealed class PresenceUseCaseTests
{
    private static readonly MailboxRef Mine = new("ME@example.com");
    private static readonly MailboxRef Bobs = new("bob@example.com");
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private readonly IUserStatusStore _store = Substitute.For<IUserStatusStore>();
    private readonly IUserAccountNames _names = Substitute.For<IUserAccountNames>();
    private readonly IAutoReplyStore _replies = Substitute.For<IAutoReplyStore>();
    private readonly IUserContext _caller = Substitute.For<IUserContext>();
    private readonly FakeTimeProvider _time = new(Now);

    public PresenceUseCaseTests()
    {
        _caller.Principal.Returns(new Principal("me-id", "me", "me@example.com", "Me"));
        _replies.GetAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _store.GetOwnAsync(Arg.Any<CancellationToken>()).Returns(UserStatus.None);
        _store.SetStatusAsync(Arg.Any<UserStatusType>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.SetPredefinedMessageAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.SetCustomMessageAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.ClearMessageAsync(Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PresenceReader Reader => new(_store, _names, _replies, _caller, _time);

    private UserStatusWriter Writer => new(_store, _caller, _time);

    private void Person(string named, string account, string id)
    {
        _names.AccountNameOfAsync(named, Arg.Any<CancellationToken>()).Returns(account);
        _names.UserOfAccountAsync(account, Arg.Any<CancellationToken>()).Returns(new DirectoryUser(id, account, account + "@example.com"));
    }

    [Fact]
    public async Task Get_reads_the_caller_without_a_user_and_anyone_with_one()
    {
        Person("bob-id", "bob", "bob-id");
        _store.GetAsync("bob", Ct).Returns(new UserStatus(UserStatusType.Away));

        (await new GetPresence(Reader).ExecuteAsync(null, Ct)).Value.Id.ShouldBe("me-id");
        (await new GetPresence(Reader).ExecuteAsync("bob-id", Ct)).Value.Availability.ShouldBe("Away");
    }

    [Fact]
    public async Task Get_by_ids_keeps_the_order_leaves_out_nobody_and_reads_each_once()
    {
        Person("bob-id", "bob", "bob-id");
        Person("carol-id", "carol", "carol-id");
        _names.AccountNameOfAsync("ghost", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));
        _store.GetAsync("bob", Arg.Any<CancellationToken>()).Returns(new UserStatus(UserStatusType.Busy));
        _store.GetAsync("carol", Arg.Any<CancellationToken>()).Returns(UserStatus.None);

        var presences = (await new GetPresencesByUserId(Reader).ExecuteAsync(["carol-id", "ghost", "bob-id", "BOB-ID", " ", "me-id"], Ct)).Value;

        presences.Select(static p => (p.Id, p.Availability)).ShouldBe([("carol-id", "Offline"), ("bob-id", "Busy"), ("me-id", "Offline")]);
        await _store.Received(1).GetAsync("bob", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_by_ids_passes_on_a_failure_other_than_nobody()
    {
        _names.AccountNameOfAsync("bob-id", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        (await new GetPresencesByUserId(Reader).ExecuteAsync(["bob-id"], Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Get_by_ids_needs_one_id_and_takes_at_most_650()
    {
        (await new GetPresencesByUserId(Reader).ExecuteAsync([" "], Ct)).Error!.Target.ShouldBe("ids");
        var many = Enumerable.Range(0, GetPresencesByUserId.MaxIds + 1).Select(static i => $"id-{i}").ToList();
        (await new GetPresencesByUserId(Reader).ExecuteAsync(many, Ct)).Error!.Target.ShouldBe("ids");
    }

    [Fact]
    public async Task Set_presence_sets_the_status_and_the_pairs_message_with_its_expiry()
    {
        var result = await new SetPresence(Writer).ExecuteAsync(Mine, new PresenceChange("Busy", "InACall", TimeSpan.FromHours(1)) { SessionId = "app" }, Ct);

        result.IsSuccess.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _store.SetStatusAsync(UserStatusType.Busy, Ct);
            _store.SetPredefinedMessageAsync("call", Now.AddHours(1), Ct);
        });
    }

    [Fact]
    public async Task A_bare_status_has_no_expiry_and_drops_an_earlier_activity_message()
    {
        _store.GetOwnAsync(Ct).Returns(new UserStatus(UserStatusType.Busy) { MessageId = "meeting" });

        (await new SetPresence(Writer).ExecuteAsync(null, new PresenceChange("Available", "Available", TimeSpan.FromHours(1)) { SessionId = "app" }, Ct)).IsSuccess.ShouldBeTrue();

        await _store.Received(1).SetStatusAsync(UserStatusType.Online, Ct);
        await _store.Received(1).ClearMessageAsync(Ct);
        await _store.DidNotReceiveWithAnyArgs().SetPredefinedMessageAsync(default!, default, Ct);
    }

    [Fact]
    public async Task A_custom_message_survives_a_status_change()
    {
        _store.GetOwnAsync(Ct).Returns(new UserStatus(UserStatusType.Online) { Message = "Focus" });

        (await new SetUserPreferredPresence(Writer).ExecuteAsync(null, new PresenceChange("DoNotDisturb", "DoNotDisturb", null), Ct)).IsSuccess.ShouldBeTrue();

        await _store.DidNotReceive().ClearMessageAsync(Ct);
    }

    [Fact]
    public async Task Setting_anyone_elses_presence_is_forbidden()
    {
        var change = new PresenceChange("Available", "Available", null) { SessionId = "app" };

        (await new SetPresence(Writer).ExecuteAsync(Bobs, change, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new SetUserPreferredPresence(Writer).ExecuteAsync(Bobs, change, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new ClearPresence(Writer).ExecuteAsync(Bobs, "app", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new ClearUserPreferredPresence(Writer).ExecuteAsync(Bobs, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new SetStatusMessage(_store, Writer, _time).ExecuteAsync(Bobs, new StatusMessageChange("hi", null), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _store.DidNotReceiveWithAnyArgs().SetStatusAsync(default, Ct);
        await _store.DidNotReceiveWithAnyArgs().SetCustomMessageAsync(default!, default, Ct);
    }

    [Theory]
    [InlineData(null, "PT1H", "sessionId")]
    [InlineData("app", "PT4M", "expirationDuration")]
    [InlineData("app", "PT5H", "expirationDuration")]
    public async Task Set_presence_needs_a_session_and_an_expiry_from_5_minutes_to_4_hours(string? session, string duration, string target)
    {
        var change = new PresenceChange("Available", "Available", System.Xml.XmlConvert.ToTimeSpan(duration)) { SessionId = session };

        (await new SetPresence(Writer).ExecuteAsync(null, change, Ct)).Error!.Target.ShouldBe(target);
    }

    [Fact]
    public async Task An_unsupported_pair_is_refused_before_any_write()
    {
        (await new SetUserPreferredPresence(Writer).ExecuteAsync(null, new PresenceChange("Available", "Presenting", null), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new SetUserPreferredPresence(Writer).ExecuteAsync(null, new PresenceChange("Away", "Away", TimeSpan.Zero), Ct)).Error!.Target.ShouldBe("expirationDuration");
        await _store.DidNotReceiveWithAnyArgs().SetStatusAsync(default, Ct);
    }

    [Fact]
    public async Task Clearing_goes_back_online_and_drops_an_activity_message()
    {
        _store.GetOwnAsync(Ct).Returns(new UserStatus(UserStatusType.Away) { MessageId = "be-right-back" });

        (await new ClearUserPreferredPresence(Writer).ExecuteAsync(Mine, Ct)).IsSuccess.ShouldBeTrue();
        (await new ClearPresence(Writer).ExecuteAsync(null, "app", Ct)).IsSuccess.ShouldBeTrue();

        await _store.Received(2).SetStatusAsync(UserStatusType.Online, Ct);
        await _store.Received(2).ClearMessageAsync(Ct);
    }

    [Fact]
    public async Task Clear_presence_needs_a_session()
    {
        (await new ClearPresence(Writer).ExecuteAsync(null, null, Ct)).Error!.Target.ShouldBe("sessionId");
    }

    [Fact]
    public async Task A_failed_read_or_status_write_stops_the_change()
    {
        _store.GetOwnAsync(Ct).Returns(Error.BackendUnavailable("down"));
        (await new ClearUserPreferredPresence(Writer).ExecuteAsync(null, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        _store.GetOwnAsync(Ct).Returns(UserStatus.None);
        _store.SetStatusAsync(UserStatusType.Busy, Ct).Returns(Error.Validation("no"));
        (await new SetUserPreferredPresence(Writer).ExecuteAsync(null, new PresenceChange("Busy", "InAMeeting", null), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _store.DidNotReceiveWithAnyArgs().SetPredefinedMessageAsync(default!, default, Ct);
    }

    [Fact]
    public async Task A_status_message_is_a_custom_message_with_its_expiry()
    {
        var expiry = Now.AddHours(2);

        (await new SetStatusMessage(_store, Writer, _time).ExecuteAsync(null, new StatusMessageChange(" Lunch ", expiry), Ct)).IsSuccess.ShouldBeTrue();

        await _store.Received(1).SetCustomMessageAsync("Lunch", expiry, Ct);
    }

    [Fact]
    public async Task An_empty_status_message_clears_it_and_a_past_expiry_is_refused()
    {
        (await new SetStatusMessage(_store, Writer, _time).ExecuteAsync(null, new StatusMessageChange("", null), Ct)).IsSuccess.ShouldBeTrue();
        await _store.Received(1).ClearMessageAsync(Ct);

        (await new SetStatusMessage(_store, Writer, _time).ExecuteAsync(null, new StatusMessageChange("hi", Now), Ct)).Error!.Target.ShouldBe("expiryDateTime");
    }
}
