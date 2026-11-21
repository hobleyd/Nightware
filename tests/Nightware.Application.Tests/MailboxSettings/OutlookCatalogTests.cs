using Nightware.Application.Common.Time;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.Tests.MailboxSettings;

public sealed class OutlookCatalogTests
{
    private static readonly MailboxRef Alice = new("alice@example.com");

    private readonly IMailboxSettingsAccess _access = Substitute.For<IMailboxSettingsAccess>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public OutlookCatalogTests() =>
        _access.CheckAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(Result.Success());

    [Fact]
    public void Every_offered_time_zone_is_one_patch_timeZone_accepts_in_both_standards()
    {
        OutlookCatalog.TimeZones.Count.ShouldBeGreaterThan(100);
        OutlookCatalog.TimeZones.Select(static z => z.Alias).ShouldBeUnique();
        foreach (var zone in OutlookCatalog.TimeZones)
        {
            GraphTimeZone.TryFind(zone.Alias, out _).ShouldBeTrue(zone.Alias);
            GraphTimeZone.TryFind(zone.IanaId, out _).ShouldBeTrue(zone.IanaId);
            GraphTimeZone.ToWindowsId(zone.Alias).ShouldBe(zone.Alias, "it reads back by the same name");
        }
    }

    [Fact]
    public void Languages_are_unique_and_their_patterns_use_plain_spaces()
    {
        OutlookCatalog.Languages.Select(static l => l.Locale).ShouldBeUnique();
        OutlookCatalog.Languages.ShouldAllBe(l => !l.TimeFormat.Contains(' ') && !l.DateFormat.Contains(' '));
        OutlookCatalog.FindLanguage("EN-us")!.DisplayName.ShouldBe("English (United States)");
        OutlookCatalog.FindLanguage("xx-YY").ShouldBeNull();
    }

    [Theory]
    [InlineData("microsoft.graph.timeZoneStandard'Iana'", TimeZoneStandard.Iana)]
    [InlineData("microsoft.graph.timeZoneStandard'Windows'", TimeZoneStandard.Windows)]
    [InlineData("'iana'", TimeZoneStandard.Iana)]
    public void The_standard_is_read_as_graph_sends_it(string argument, TimeZoneStandard expected) =>
        OutlookCatalog.ParseStandard(argument).Value.ShouldBe(expected);

    [Theory]
    [InlineData("microsoft.graph.timeZoneStandard'Olson'")]
    [InlineData("1")]
    [InlineData("")]
    public void Any_other_standard_is_refused(string argument) =>
        OutlookCatalog.ParseStandard(argument).Error!.Kind.ShouldBe(ErrorKind.Validation);

    [Fact]
    public async Task Iana_ids_are_listed_once_as_their_own_display_name()
    {
        var zones = (await new GetSupportedTimeZones(_access).ExecuteAsync(Alice, TimeZoneStandard.Iana, Ct)).Value;

        zones.Select(static z => z.Alias).ShouldBeUnique();
        zones.ShouldAllBe(z => z.Alias == z.DisplayName);
        zones.ShouldContain(z => z.Alias == "Australia/Sydney");
    }

    [Fact]
    public async Task A_mailbox_the_caller_may_not_open_lists_nothing()
    {
        _access.CheckAsync(Alice, Arg.Any<CancellationToken>()).Returns(Result.Failure(Error.Forbidden("no")));

        (await new GetSupportedTimeZones(_access).ExecuteAsync(Alice, TimeZoneStandard.Windows, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new GetSupportedLanguages(_access).ExecuteAsync(Alice, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public void Without_settings_of_their_own_a_mailbox_has_its_languages_formats_and_exchanges_working_hours_in_its_zone()
    {
        var composed = MailboxSettingsComposer.Compose(new AutoReplySetting(AutoReplyStatus.Disabled), new UserPreferences("Australia/Perth", "de-DE"));

        composed.Language.ShouldBe(new MailboxLanguage("de-DE", "Deutsch (Deutschland)"));
        (composed.DateFormat, composed.TimeFormat).ShouldBe(("dd.MM.yyyy", "HH:mm"));
        composed.WorkingHours!.TimeZone.ShouldBe("Australia/Perth");
        composed.WorkingHours.StartTime.ShouldBe(new TimeOnly(8, 0));
    }

    [Fact]
    public void Their_own_settings_win_and_an_unknown_locale_is_named_by_itself()
    {
        var own = new WorkingHours([DayOfWeek.Saturday], new TimeOnly(10, 0), new TimeOnly(14, 0), "Europe/London");
        var composed = MailboxSettingsComposer.Compose(new AutoReplySetting(AutoReplyStatus.Disabled),
            new UserPreferences(null, "mi-NZ") { WorkingHours = own, DateFormat = "yyyy-MM-dd", TimeFormat = "HH:mm" });

        composed.Language.ShouldBe(new MailboxLanguage("mi-NZ", "mi-NZ"));
        (composed.DateFormat, composed.TimeFormat, composed.WorkingHours).ShouldBe(("yyyy-MM-dd", "HH:mm", own));
    }

    [Fact]
    public void A_mailbox_the_directory_does_not_know_reads_as_en_us_in_utc()
    {
        var composed = MailboxSettingsComposer.Compose(new AutoReplySetting(AutoReplyStatus.Disabled), null);

        composed.Language.ShouldBe(MailboxLanguage.Default);
        (composed.DateFormat, composed.TimeFormat, composed.WorkingHours!.TimeZone).ShouldBe(("M/d/yyyy", "h:mm tt", "UTC"));
    }
}
