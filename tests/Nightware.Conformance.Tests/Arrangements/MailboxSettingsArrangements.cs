using Nightware.Application.Directory;
using Nightware.Application.MailboxSettings;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 5B mailbox settings: automatic replies (mail store) and time zone (directory).</summary>
public sealed class MailboxSettingsArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");

    /// <summary>As the mail store holds it: the schedule in UTC (20–27 Sept, Sydney time).</summary>
    private static readonly AutoReplySetting Stored = new(AutoReplyStatus.Scheduled)
    {
        ExternalAudience = ExternalAudience.ContactsOnly,
        InternalReplyMessage = "<div>Away.</div>",
        ExternalReplyMessage = "<div>Out of the office.</div>",
        ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 19, 14, 0, 0), "UTC"),
        ScheduledEndDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 27, 13, 59, 59), "UTC"),
    };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("mailbox-settings.get", static fakes =>
        {
            fakes.For<IMailboxSettingsAccess>().CheckAsync(Me, Arg.Any<CancellationToken>()).Returns(Result.Success());
            fakes.For<IAutoReplyStore>().GetAsync(Me, Arg.Any<CancellationToken>()).Returns(Stored);
            fakes.For<IUserPreferencesStore>().GetAsync(Me, Arg.Any<CancellationToken>()).Returns(new UserPreferences("Australia/Sydney", "en"));
        });

        yield return new ConformanceArrangement("mailbox-settings.update", static fakes =>
        {
            fakes.For<IMailboxSettingsAccess>().CheckAsync(Me, Arg.Any<CancellationToken>()).Returns(Result.Success());
            fakes.For<IAutoReplyStore>()
                .SetAsync(Me, Arg.Is<AutoReplySetting>(s => s.Status == AutoReplyStatus.Scheduled
                    && s.ExternalAudience == ExternalAudience.ContactsOnly
                    && s.ExternalReplyMessage == "<div>Out of the office.</div>"
                    && s.ScheduledStartDateTime == new DateTimeTimeZone(new DateTime(2026, 9, 20), "AUS Eastern Standard Time")), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(Stored);
            fakes.For<IInternalDomains>().ForAsync(Me, Arg.Any<CancellationToken>()).Returns(["example.com"]);
            fakes.For<IUserPreferencesStore>().GetAsync(Me, Arg.Any<CancellationToken>()).Returns(new UserPreferences("Australia/Sydney", "en"));
        });
    }
}
