using System.Text.Json.Nodes;

using Nightware.Application.Directory;
using Nightware.Application.MailboxSettings;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 18: single mailbox settings, and the supported time zones and languages.</summary>
public sealed class MailboxSettingsParityArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");

    private static readonly AutoReplySetting Stored = new(AutoReplyStatus.Scheduled)
    {
        ExternalAudience = ExternalAudience.ContactsOnly,
        InternalReplyMessage = "<div>Away.</div>",
        ExternalReplyMessage = "<div>Out of the office.</div>",
        ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 19, 14, 0, 0), "UTC"),
        ScheduledEndDateTime = new DateTimeTimeZone(new DateTime(2026, 9, 27, 13, 59, 59), "UTC"),
    };

    /// <summary>Sydney, Australian English, no working hours or formats of their own.</summary>
    private static void Settings(PortFakes fakes)
    {
        fakes.For<IMailboxSettingsAccess>().CheckAsync(Me, Arg.Any<CancellationToken>()).Returns(Result.Success());
        fakes.For<IAutoReplyStore>().GetAsync(Me, Arg.Any<CancellationToken>()).Returns(Stored);
        fakes.For<IUserPreferencesStore>().GetAsync(Me, Arg.Any<CancellationToken>()).Returns(new UserPreferences("Australia/Sydney", "en-AU"));
    }

    private static void Access(PortFakes fakes) =>
        fakes.For<IMailboxSettingsAccess>().CheckAsync(Me, Arg.Any<CancellationToken>()).Returns(Result.Success());

    /// <summary>The fixtures keep the first entries of the fixed lists.</summary>
    private static void FirstOnly(JsonNode expected, JsonNode actual)
    {
        var keep = expected["value"]!.AsArray().Count;
        var value = actual["value"]!.AsArray();
        while (value.Count > keep)
        {
            value.RemoveAt(value.Count - 1);
        }
    }

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        foreach (var setting in new[] { "automatic-replies", "time-zone", "language", "working-hours", "date-format", "time-format" })
        {
            yield return new ConformanceArrangement($"mailbox-settings.{setting}.get", Settings);
        }

        yield return new ConformanceArrangement("outlook.supported-time-zones", Access) { Normalise = FirstOnly };
        yield return new ConformanceArrangement("outlook.supported-time-zones-standard", Access) { Normalise = FirstOnly };
        yield return new ConformanceArrangement("outlook.supported-languages", Access) { Normalise = FirstOnly };
    }
}
