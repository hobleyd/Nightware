using Nightware.Application.Common.Time;
using Nightware.Application.Directory;
using Nightware.Application.Presence;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.Presence;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 25: presence over Nextcloud user status (ADR 0009 D6). The fixtures are constructed from Graph's reference pages;
/// the user ids are the pages' own, and the caller (<c>me@example.com</c>) is the pages' <c>fa8bf3dc-…</c> on the write
/// routes. Graph's <c>sequenceNumber</c> has no Nextcloud counterpart and is not emitted.
/// </summary>
public sealed class PresenceArrangements : IConformanceArrangements
{
    private const string Me = "fa8bf3dc-eca7-46b7-bad1-db199b62afc3";
    private const string Megan = "66825e03-7ef5-42da-9069-724602c31f6b";
    private const string Carol = "dc74d9bb-6afe-433d-8eaa-e39d80d3a647";
    private const string Session = "22553876-f5ab-4529-bffb-cfe50aa89f87";

    private static readonly string[] Unsequenced = ["sequenceNumber"];

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("presence.get", static f =>
        {
            f.For<IUserDirectory>().GetAsync(Megan, Any).Returns(Person(Megan, "megan"));
            Account(f, Megan, "megan", "megan@example.com");
            f.For<IUserStatusStore>().GetAsync("megan", Any).Returns(new UserStatus(UserStatusType.DoNotDisturb));
        })
        { IgnorePaths = Unsequenced };

        yield return new ConformanceArrangement("presence.get-by-id", static f =>
        {
            Account(f, Carol, "carol", Carol);
            f.For<IUserStatusStore>().GetAsync("carol", Any).Returns(new UserStatus(UserStatusType.Online) { Message = "Hey I am available now" });
        })
        { IgnorePaths = Unsequenced };

        yield return new ConformanceArrangement("presence.get-by-ids", static f =>
        {
            Account(f, Me, "adele", Me);
            Account(f, Megan, "megan", Megan);

            // Another user's public status has no message id: the meeting is known by its icon.
            f.For<IUserStatusStore>().GetAsync("adele", Any)
                .Returns(new UserStatus(UserStatusType.Busy) { Message = "In a meeting", Icon = PredefinedMessages.MeetingIcon });
            f.For<IUserStatusStore>().GetAsync("megan", Any).Returns(new UserStatus(UserStatusType.Away));
        });

        yield return Write("presence.set", static store =>
            store.SetStatusAsync(UserStatusType.Online, Any).Returns(Result.Success()));
        yield return Write("presence.clear", static store =>
            store.SetStatusAsync(UserStatusType.Online, Any).Returns(Result.Success()));
        yield return Write("presence.set-preferred", static store =>
            store.SetStatusAsync(UserStatusType.DoNotDisturb, Any).Returns(Result.Success()));
        yield return Write("presence.clear-preferred", static store =>
            store.SetStatusAsync(UserStatusType.Online, Any).Returns(Result.Success()));
        yield return Write("presence.set-status-message", static store =>
        {
            var expiry = GraphTimeZone.ToInstant(new DateTime(2030, 10, 18, 17, 5, 33).AddTicks(2079781), "Pacific Standard Time");
            store.SetCustomMessageAsync("Hey I'm currently in a meeting.", expiry, Any).Returns(Result.Success());
        });
    }

    private static CancellationToken Any => Arg.Any<CancellationToken>();

    private static DirectoryUser Person(string id, string name) =>
        new(id, name, $"{name}@example.com") { Mail = $"{name}@example.com" };

    private static void Account(PortFakes f, string id, string accountName, string named)
    {
        f.For<IUserAccountNames>().AccountNameOfAsync(named, Any).Returns(accountName);
        f.For<IUserAccountNames>().UserOfAccountAsync(accountName, Any).Returns(Person(id, accountName));
    }

    /// <summary>A write on <c>/users/{caller's id}</c>: the id resolves to the caller's mailbox, whose status has no message.</summary>
    private static ConformanceArrangement Write(string operation, Action<IUserStatusStore> arrange) =>
        new(operation, f =>
        {
            f.For<IUserDirectory>().GetAsync(Me, Any).Returns(new DirectoryUser(Me, "Me Example", "me@example.com") { Mail = "me@example.com" });
            f.For<IUserStatusStore>().GetOwnAsync(Any).Returns(UserStatus.None);
            arrange(f.For<IUserStatusStore>());
        });
}
