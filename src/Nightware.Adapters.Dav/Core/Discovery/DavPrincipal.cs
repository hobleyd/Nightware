namespace Nightware.Adapters.Dav.Core.Discovery;

/// <summary>
/// A CalDAV/CardDAV principal and the collections hanging off it. Every href is
/// server-absolute as Sabre returned it and can be passed straight to <see cref="IDavClient"/>.
/// </summary>
/// <param name="Href">The principal URL, e.g. <c>/remote.php/dav/principals/users/alice/</c>.</param>
/// <param name="IsCurrentUser">True when this is the caller's own principal.</param>
public sealed record DavPrincipal(string Href, bool IsCurrentUser)
{
    public string? DisplayName { get; init; }

    public string? CalendarHomeSet { get; init; }

    public string? AddressBookHomeSet { get; init; }

    public string? ScheduleOutboxUrl { get; init; }

    public string? ScheduleInboxUrl { get; init; }

    /// <summary><c>mailto:</c> (and other) URIs from <c>calendar-user-address-set</c>.</summary>
    public IReadOnlyList<string> CalendarUserAddresses { get; init; } = [];

    /// <summary>RFC 6638 <c>calendar-user-type</c>: INDIVIDUAL, ROOM, RESOURCE, GROUP…</summary>
    public string? CalendarUserType { get; init; }

    /// <summary>Email addresses taken from <see cref="CalendarUserAddresses"/> (and Sabre's <c>email-address</c>).</summary>
    public IReadOnlyList<string> EmailAddresses { get; init; } = [];

    public bool IsRoomOrResource =>
        string.Equals(CalendarUserType, "ROOM", StringComparison.OrdinalIgnoreCase)
        || string.Equals(CalendarUserType, "RESOURCE", StringComparison.OrdinalIgnoreCase);

    public bool HasAddress(string address) =>
        EmailAddresses.Any(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));
}
