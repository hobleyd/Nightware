using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Calendars;

/// <summary>Who a calendar is shared with (Nextcloud CalDAV sharing; ADR 0006 D6).</summary>
public interface ICalendarPermissionStore
{
    Task<Result<IReadOnlyList<CalendarShare>>> ListAsync(MailboxRef mailbox, CalendarId calendar, CancellationToken cancellationToken);

    /// <summary>Shares with <paramref name="address"/>, a directory user, or changes their role.</summary>
    Task<Result<CalendarShare>> GrantAsync(MailboxRef mailbox, CalendarId calendar, string address, CalendarRole role, CancellationToken cancellationToken);

    Task<Result> RevokeAsync(MailboxRef mailbox, CalendarId calendar, string permissionId, CancellationToken cancellationToken);
}

/// <summary>Graph <c>calendars/{id}/calendarPermissions</c>: only <c>read</c> and <c>write</c> exist in Nextcloud.</summary>
public sealed class CalendarPermissionsUseCase(ICalendarPermissionStore store)
{
    public Task<Result<IReadOnlyList<CalendarShare>>> ListAsync(MailboxRef mailbox, CalendarId calendar, CancellationToken cancellationToken) =>
        store.ListAsync(mailbox, calendar, cancellationToken);

    public async Task<Result<CalendarShare>> CreateAsync(MailboxRef mailbox, CalendarId calendar, string? address, string? role, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return Error.Validation("A permission needs the emailAddress of the person to share with.", "emailAddress");
        }

        var parsed = RoleOf(role ?? "read");
        return parsed.IsSuccess
            ? await store.GrantAsync(mailbox, calendar, address.Trim(), parsed.Value, cancellationToken).ConfigureAwait(false)
            : parsed.Error;
    }

    public async Task<Result<CalendarShare>> UpdateAsync(MailboxRef mailbox, CalendarId calendar, string permissionId, string? role, CancellationToken cancellationToken)
    {
        var parsed = RoleOf(role ?? string.Empty);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        var existing = await store.ListAsync(mailbox, calendar, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.Error;
        }

        return existing.Value.FirstOrDefault(p => p.Id == permissionId) is { } permission
            ? await store.GrantAsync(mailbox, calendar, permission.EmailAddress.Address, parsed.Value, cancellationToken).ConfigureAwait(false)
            : Error.NotFound("The permission does not exist.");
    }

    public Task<Result> DeleteAsync(MailboxRef mailbox, CalendarId calendar, string permissionId, CancellationToken cancellationToken) =>
        store.RevokeAsync(mailbox, calendar, permissionId, cancellationToken);

    private static Result<CalendarRole> RoleOf(string role) => role.Trim().ToUpperInvariant() switch
    {
        "READ" => CalendarRole.Read,
        "WRITE" => CalendarRole.Write,
        _ => Error.Unsupported($"'{role}' cannot be granted here: Nextcloud shares calendars read or write.", "role"),
    };
}
