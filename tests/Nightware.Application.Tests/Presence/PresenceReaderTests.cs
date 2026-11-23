using Microsoft.Extensions.Time.Testing;

using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Application.MailboxSettings;
using Nightware.Application.Presence;
using Nightware.Domain.Calendar;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;
using Nightware.Domain.Presence;

namespace Nightware.Application.Tests.Presence;

public sealed class PresenceReaderTests
{
    private static readonly MailboxRef MyMailbox = new("me@example.com");

    private readonly IUserStatusStore _store = Substitute.For<IUserStatusStore>();
    private readonly IUserAccountNames _names = Substitute.For<IUserAccountNames>();
    private readonly IAutoReplyStore _replies = Substitute.For<IAutoReplyStore>();
    private readonly IUserContext _caller = Substitute.For<IUserContext>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));

    public PresenceReaderTests()
    {
        _caller.Principal.Returns(new Principal("me-id", "me", "me@example.com", "Me"));
        _replies.GetAsync(MyMailbox, Arg.Any<CancellationToken>()).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _store.GetOwnAsync(Arg.Any<CancellationToken>()).Returns(new UserStatus(UserStatusType.Invisible));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PresenceReader Reader => new(_store, _names, _replies, _caller, _time);

    private void Person(string named, string account, string id)
    {
        _names.AccountNameOfAsync(named, Arg.Any<CancellationToken>()).Returns(account);
        _names.UserOfAccountAsync(account, Arg.Any<CancellationToken>()).Returns(new DirectoryUser(id, account, account + "@example.com"));
    }

    [Fact]
    public async Task The_callers_own_is_their_private_status_under_their_id()
    {
        var presence = (await Reader.OwnAsync(Ct)).Value;

        presence.ShouldBe(new UserPresence("me-id", "Offline", "OffWork"));
        await _names.DidNotReceiveWithAnyArgs().AccountNameOfAsync(default!, Ct);
    }

    [Theory]
    [InlineData("me-id")]
    [InlineData("ME@example.com")]
    public async Task Naming_the_caller_reads_their_own(string user)
    {
        (await Reader.OfAsync(user, Ct)).Value.Activity.ShouldBe("OffWork");
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, Ct);
    }

    [Fact]
    public async Task The_caller_by_another_name_is_still_their_own()
    {
        Person("me.alias@example.com", "me", "me-id");

        (await Reader.OfAsync("me.alias@example.com", Ct)).Value.Activity.ShouldBe("OffWork");
    }

    [Fact]
    public async Task Another_user_is_read_by_account_name_and_reported_by_directory_id()
    {
        Person("bob@example.com", "bob", "bob-id");
        _store.GetAsync("bob", Ct).Returns(new UserStatus(UserStatusType.DoNotDisturb));

        var presence = (await Reader.OfAsync("bob@example.com", Ct)).Value;

        presence.ShouldBe(new UserPresence("bob-id", "DoNotDisturb", "DoNotDisturb"));
        await _replies.DidNotReceiveWithAnyArgs().GetAsync(default, Ct);
    }

    [Fact]
    public async Task Nobody_in_the_directory_is_not_found()
    {
        _names.AccountNameOfAsync("ghost", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));

        (await Reader.OfAsync("ghost", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_directory_failure_after_the_account_name_is_passed_on()
    {
        _names.AccountNameOfAsync("bob-id", Arg.Any<CancellationToken>()).Returns("bob");
        _names.UserOfAccountAsync("bob", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        (await Reader.OfAsync("bob-id", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_status_failure_is_passed_on()
    {
        Person("bob-id", "bob", "bob-id");
        _store.GetAsync("bob", Ct).Returns(Error.BackendUnavailable("down"));

        (await Reader.OfAsync("bob-id", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task The_callers_status_failure_is_passed_on()
    {
        _store.GetOwnAsync(Ct).Returns(Error.BackendUnavailable("down"));

        (await Reader.OwnAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Replies_always_on_are_out_of_office_with_the_internal_message()
    {
        _replies.GetAsync(MyMailbox, Ct).Returns(new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) { InternalReplyMessage = "Away until Monday" });

        (await Reader.OwnAsync(Ct)).Value.OutOfOffice.ShouldBe(new OutOfOfficeSettings(true, "Away until Monday"));
    }

    [Theory]
    [InlineData(9, 11, true)]
    [InlineData(10, 11, true)]
    [InlineData(11, 12, false)]
    [InlineData(8, 10, false)]
    public async Task Scheduled_replies_are_out_of_office_inside_their_window(int startHour, int endHour, bool expected)
    {
        _replies.GetAsync(MyMailbox, Ct).Returns(new AutoReplySetting(AutoReplyStatus.Scheduled)
        {
            InternalReplyMessage = "Out",
            ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 11, 23, startHour, 0, 0), "UTC"),
            ScheduledEndDateTime = new DateTimeTimeZone(new DateTime(2026, 11, 23, endHour, 0, 0), "UTC"),
        });

        (await Reader.OwnAsync(Ct)).Value.OutOfOffice.IsOutOfOffice.ShouldBe(expected);
    }

    [Fact]
    public async Task A_mail_server_that_cant_answer_leaves_the_caller_in_the_office()
    {
        _replies.GetAsync(MyMailbox, Ct).Returns(Error.BackendUnavailable("down"));

        var presence = await Reader.OwnAsync(Ct);

        presence.IsSuccess.ShouldBeTrue();
        presence.Value.OutOfOffice.ShouldBe(OutOfOfficeSettings.NotOutOfOffice);
    }

    [Fact]
    public async Task A_caller_without_a_mailbox_is_in_the_office()
    {
        _caller.Principal.Returns(new Principal("me-id", "me", string.Empty, "Me"));

        (await Reader.OwnAsync(Ct)).Value.OutOfOffice.ShouldBe(OutOfOfficeSettings.NotOutOfOffice);
        await _replies.DidNotReceiveWithAnyArgs().GetAsync(default, Ct);
    }
}
