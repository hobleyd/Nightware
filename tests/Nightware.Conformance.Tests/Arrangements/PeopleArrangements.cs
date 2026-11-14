using Microsoft.Extensions.DependencyInjection;

using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Application.Directory;
using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Contacts;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 5A ranked people.</summary>
public sealed class PeopleArrangements : IConformanceArrangements
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        // A correspondent mailed often and just now earns the full correspondence score, 20.0.
        yield return new ConformanceArrangement("people.list", static fakes =>
        {
            var me = new MailboxRef("me@example.com");
            fakes.For<IContactStore>().ListAsync(me, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Page<Contact>.Empty);
            fakes.For<IUserDirectory>().ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Page<DirectoryUser>.Empty);
            fakes.For<IRecentCorrespondents>().ListAsync(me, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Result<IReadOnlyList<Correspondent>>.Success([new Correspondent(new EmailAddress("alex@example.com", "Alex Sender"), 42, Now)]));
        })
        {
            // Graph person ids are opaque; Nightware derives its own stable id from the address.
            IgnorePaths = ["value/*/id"],
            Services = static services => services.AddSingleton<TimeProvider>(new FixedTime(Now)),
        };
    }

    /// <summary>Pins "now" so recency scoring is deterministic.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
