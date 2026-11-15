using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Calendar;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.Tests.MailboxSettings;

public sealed class MailboxSettingsUseCaseTests
{
    private static readonly MailboxRef Alice = new("alice@example.com");
    private static readonly MailboxRef Shared = new("team@example.com");

    private readonly IMailboxSettingsAccess _access = Substitute.For<IMailboxSettingsAccess>();
    private readonly IAutoReplyStore _replies = Substitute.For<IAutoReplyStore>();
    private readonly IUserPreferencesStore _preferences = Substitute.For<IUserPreferencesStore>();
    private readonly IUserContext _caller = Substitute.For<IUserContext>();

    public MailboxSettingsUseCaseTests()
    {
        _caller.Principal.Returns(new Principal("sub-alice", "alice", "alice@example.com", "Alice"));
        _access.CheckAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A schedule as the mail store reports it: instants in UTC.</summary>
    private static AutoReplySetting StoredSchedule => new(AutoReplyStatus.Scheduled)
    {
        InternalReplyMessage = "<p>Away</p>",
        ExternalReplyMessage = "<p>Away</p>",
        ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 19, 14, 0, 0), "UTC"),
        ScheduledEndDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 27, 13, 59, 59), "UTC"),
    };

    [Fact]
    public async Task Get_expresses_the_schedule_in_the_mailbox_zone_by_its_windows_id()
    {
        _replies.GetAsync(Alice, Ct).Returns(StoredSchedule);
        _preferences.GetAsync(Alice, Ct).Returns(new UserPreferences("Australia/Sydney", "en"));

        var result = await Get().ExecuteAsync(Alice, Ct);

        result.Value.TimeZone.ShouldBe("AUS Eastern Standard Time");
        result.Value.AutomaticReplies.ScheduledStartDateTime.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 9, 20, 0, 0, 0), "AUS Eastern Standard Time"));
        result.Value.AutomaticReplies.ScheduledEndDateTime.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 9, 27, 23, 59, 59), "AUS Eastern Standard Time"));
        result.Value.AutomaticReplies.InternalReplyMessage.ShouldBe("<p>Away</p>");
    }

    [Fact]
    public async Task Get_keeps_a_stored_windows_id()
    {
        _replies.GetAsync(Alice, Ct).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.GetAsync(Alice, Ct).Returns(new UserPreferences("GMT Standard Time", null));

        var result = await Get().ExecuteAsync(Alice, Ct);

        result.Value.TimeZone.ShouldBe("GMT Standard Time");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Mars/Olympus_Mons")]
    public async Task Get_falls_back_to_utc_without_a_usable_zone(string? zone)
    {
        _replies.GetAsync(Alice, Ct).Returns(StoredSchedule);
        _preferences.GetAsync(Alice, Ct).Returns(new UserPreferences(zone, null));

        var result = await Get().ExecuteAsync(Alice, Ct);

        result.Value.TimeZone.ShouldBe("UTC");
        result.Value.AutomaticReplies.ScheduledStartDateTime.ShouldBe(StoredSchedule.ScheduledStartDateTime);
    }

    [Fact]
    public async Task Get_treats_a_mailbox_unknown_to_the_directory_as_utc()
    {
        _replies.GetAsync(Shared, Ct).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.GetAsync(Shared, Ct).Returns(Error.NotFound("no such user"));

        var result = await Get().ExecuteAsync(Shared, Ct);

        result.Value.TimeZone.ShouldBe("UTC");
    }

    [Fact]
    public async Task Get_passes_other_directory_failures_through()
    {
        _replies.GetAsync(Alice, Ct).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.GetAsync(Alice, Ct).Returns(Error.BackendUnavailable("keycloak down"));

        var result = await Get().ExecuteAsync(Alice, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Get_never_reads_the_directory_when_the_mail_store_refuses_the_mailbox()
    {
        _replies.GetAsync(Shared, Ct).Returns(Error.Forbidden("not yours"));

        var result = await Get().ExecuteAsync(Shared, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _preferences.DidNotReceiveWithAnyArgs().GetAsync(default, Ct);
    }

    [Fact]
    public async Task Update_replaces_the_automatic_replies_and_returns_them_in_the_mailbox_zone()
    {
        var requested = StoredSchedule with
        {
            ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 20), "AUS Eastern Standard Time"),
            ScheduledEndDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 27, 23, 59, 59), "AUS Eastern Standard Time"),
        };
        _replies.SetAsync(Alice, requested, Ct).Returns(StoredSchedule);
        _preferences.GetAsync(Alice, Ct).Returns(new UserPreferences("Australia/Sydney", null));

        var result = await Update().ExecuteAsync(Alice, new MailboxSettingsPatch { AutomaticReplies = requested }, Ct);

        result.Value.AutomaticReplies.ScheduledStartDateTime.ShouldBe(requested.ScheduledStartDateTime);
        await _preferences.DidNotReceiveWithAnyArgs().SetAsync(default, null!, Ct);
    }

    [Fact]
    public async Task Update_stores_the_time_zone_as_iana()
    {
        _replies.GetAsync(Alice, Ct).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.SetAsync(Alice, new UserPreferencesPatch { TimeZone = "Europe/London" }, Ct).Returns(new UserPreferences("Europe/London", null));

        var result = await Update().ExecuteAsync(Alice, new MailboxSettingsPatch { TimeZone = "GMT Standard Time" }, Ct);

        result.Value.TimeZone.ShouldBe("GMT Standard Time");
    }

    [Fact]
    public async Task Update_matches_the_caller_case_insensitively()
    {
        _replies.GetAsync(new MailboxRef("Alice@Example.com"), Ct).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.SetAsync(new MailboxRef("Alice@Example.com"), Arg.Any<UserPreferencesPatch>(), Ct).Returns(new UserPreferences("Europe/London", null));

        var result = await Update().ExecuteAsync(new MailboxRef("Alice@Example.com"), new MailboxSettingsPatch { TimeZone = "Europe/London" }, Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_refuses_to_change_another_mailboxs_time_zone_without_touching_either_store()
    {
        var result = await Update().ExecuteAsync(Shared, new MailboxSettingsPatch { TimeZone = "Europe/London", AutomaticReplies = StoredSchedule }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _replies.DidNotReceiveWithAnyArgs().SetAsync(default, null!, Ct);
        await _preferences.DidNotReceiveWithAnyArgs().SetAsync(default, null!, Ct);
    }

    [Fact]
    public async Task Update_of_another_mailbox_reads_the_directory_only_after_the_mail_store_accepted_the_write()
    {
        _replies.SetAsync(Shared, Arg.Any<AutoReplySetting>(), Ct).Returns(Error.Forbidden("read only"));

        var result = await Update().ExecuteAsync(Shared, new MailboxSettingsPatch { AutomaticReplies = new AutoReplySetting(AutoReplyStatus.Disabled) }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _preferences.DidNotReceiveWithAnyArgs().GetAsync(default, Ct);
    }

    [Fact]
    public async Task Update_of_another_mailboxs_automatic_replies_is_up_to_the_mail_store()
    {
        _replies.SetAsync(Shared, Arg.Any<AutoReplySetting>(), Ct).Returns(new AutoReplySetting(AutoReplyStatus.AlwaysEnabled));
        _preferences.GetAsync(Shared, Ct).Returns(Error.NotFound("no such user"));

        var result = await Update().ExecuteAsync(Shared, new MailboxSettingsPatch { AutomaticReplies = new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) }, Ct);

        result.Value.AutomaticReplies.Status.ShouldBe(AutoReplyStatus.AlwaysEnabled);
    }

    [Fact]
    public async Task Update_rejects_an_empty_patch()
    {
        var result = await Update().ExecuteAsync(Alice, new MailboxSettingsPatch(), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Update_rejects_an_unknown_time_zone()
    {
        var result = await Update().ExecuteAsync(Alice, new MailboxSettingsPatch { TimeZone = "Mars/Olympus_Mons" }, Ct);

        result.Error!.Target.ShouldBe("timeZone");
    }

    [Fact]
    public async Task Update_rejects_a_schedule_in_an_unknown_zone()
    {
        var setting = new AutoReplySetting(AutoReplyStatus.Scheduled) { ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 20), "Nowhere") };

        var result = await Update().ExecuteAsync(Alice, new MailboxSettingsPatch { AutomaticReplies = setting }, Ct);

        result.Error!.Target.ShouldBe("scheduledStartDateTime");
    }

    [Fact]
    public async Task Update_rejects_a_schedule_that_ends_before_it_starts()
    {
        var setting = new AutoReplySetting(AutoReplyStatus.Scheduled)
        {
            ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 20), "UTC"),
            ScheduledEndDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 20, 9, 0, 0), "AUS Eastern Standard Time"),
        };

        var result = await Update().ExecuteAsync(Alice, new MailboxSettingsPatch { AutomaticReplies = setting }, Ct);

        result.Error!.Target.ShouldBe("scheduledEndDateTime");
    }

    [Fact]
    public async Task Update_passes_mail_store_failures_through()
    {
        _replies.SetAsync(Alice, Arg.Any<AutoReplySetting>(), Ct).Returns(Error.BackendUnavailable("stalwart down"));

        var result = await Update().ExecuteAsync(Alice, new MailboxSettingsPatch { AutomaticReplies = new AutoReplySetting(AutoReplyStatus.Disabled) }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Update_passes_directory_failures_through()
    {
        _replies.GetAsync(Alice, Ct).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.SetAsync(Alice, Arg.Any<UserPreferencesPatch>(), Ct).Returns(Error.Forbidden("no manage-users"));

        var result = await Update().ExecuteAsync(Alice, new MailboxSettingsPatch { TimeZone = "UTC" }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Theory]
    [InlineData(ErrorKind.Forbidden)]
    [InlineData(ErrorKind.NotFound)]
    public async Task Get_touches_no_store_when_access_is_refused(ErrorKind refusal)
    {
        _access.CheckAsync(Shared, Ct).Returns(new Error(refusal, "refused", "not yours"));
        // Both stores would answer: only the access check may stop the read.
        _replies.GetAsync(Shared, Ct).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.GetAsync(Shared, Ct).Returns(new UserPreferences("Australia/Sydney", null));

        var result = await Get().ExecuteAsync(Shared, Ct);

        result.Error!.Kind.ShouldBe(refusal);
        await _replies.DidNotReceiveWithAnyArgs().GetAsync(default, Ct);
        await _preferences.DidNotReceiveWithAnyArgs().GetAsync(default, Ct);
    }

    [Fact]
    public async Task Get_checks_access_before_reading_either_store()
    {
        _replies.GetAsync(Alice, Ct).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.GetAsync(Alice, Ct).Returns(new UserPreferences("UTC", null));

        (await Get().ExecuteAsync(Alice, Ct)).IsSuccess.ShouldBeTrue();

        Received.InOrder(() =>
        {
            _access.CheckAsync(Alice, Ct);
            _replies.GetAsync(Alice, Ct);
            _preferences.GetAsync(Alice, Ct);
        });
    }

    [Fact]
    public async Task Update_touches_no_store_when_access_is_refused()
    {
        _access.CheckAsync(Shared, Ct).Returns(Error.Forbidden("not yours"));
        _replies.SetAsync(Shared, Arg.Any<AutoReplySetting>(), Ct).Returns(StoredSchedule);
        _preferences.GetAsync(Shared, Ct).Returns(new UserPreferences("UTC", null));

        var result = await Update().ExecuteAsync(Shared, new MailboxSettingsPatch { AutomaticReplies = StoredSchedule }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _replies.DidNotReceiveWithAnyArgs().SetAsync(default, null!, Ct);
        await _replies.DidNotReceiveWithAnyArgs().GetAsync(default, Ct);
        await _preferences.DidNotReceiveWithAnyArgs().GetAsync(default, Ct);
        await _preferences.DidNotReceiveWithAnyArgs().SetAsync(default, null!, Ct);
    }

    [Fact]
    public async Task Update_checks_access_before_writing_either_store()
    {
        _replies.GetAsync(Alice, Ct).Returns(new AutoReplySetting(AutoReplyStatus.Disabled));
        _preferences.SetAsync(Alice, Arg.Any<UserPreferencesPatch>(), Ct).Returns(new UserPreferences("UTC", null));

        (await Update().ExecuteAsync(Alice, new MailboxSettingsPatch { TimeZone = "UTC" }, Ct)).IsSuccess.ShouldBeTrue();

        Received.InOrder(() =>
        {
            _access.CheckAsync(Alice, Ct);
            _replies.GetAsync(Alice, Ct);
            _preferences.SetAsync(Alice, Arg.Any<UserPreferencesPatch>(), Ct);
        });
    }

    private GetMailboxSettings Get() => new(_access, _replies, _preferences);

    private UpdateMailboxSettings Update() => new(_access, _replies, _preferences, _caller);
}
