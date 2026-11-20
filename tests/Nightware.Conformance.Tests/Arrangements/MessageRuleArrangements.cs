using Nightware.Application.Mail.Rules;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 12 Inbox rules (ADR 0006 D5), against captures from Microsoft 365.</summary>
public sealed class MessageRuleArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");

    private static readonly MessageRule Captured = new("AAMk-id-0011=", "Nightware eb09af", 1, true)
    {
        Conditions = new MessageRulePredicates { SubjectContains = ["nw-capture-eb09af"] },
        Exceptions = new MessageRulePredicates { HasAttachments = true },
        Actions = new MessageRuleActions { MoveToFolder = new FolderId("AAMk-parentFolderId-0001="), MarkAsRead = true, StopProcessingRules = true },
    };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("mail.rules.list", static fakes => Stored(fakes));

        // The new rule's id is the façade's own; Exchange renumbered sequence 2 to 1, as the façade does.
        yield return new ConformanceArrangement("mail.rules.create", static fakes => Stored(fakes))
        {
            IgnorePaths = ["id"],
        };

        yield return new ConformanceArrangement("mail.rules.get", static fakes => Stored(fakes, Captured));

        yield return new ConformanceArrangement("mail.rules.update", static fakes => Stored(fakes, Captured));

        yield return new ConformanceArrangement("mail.rules.delete", static fakes => Stored(fakes, Captured));
    }

    private static void Stored(PortFakes fakes, params MessageRule[] rules)
    {
        var store = fakes.For<IMessageRuleStore>();
        store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<MessageRule>>.Success(rules));
        store.SaveAsync(Me, Arg.Any<IReadOnlyList<MessageRule>>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
    }
}
