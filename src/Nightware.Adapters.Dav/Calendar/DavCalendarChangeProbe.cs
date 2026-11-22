using System.Globalization;
using System.Text;

using Nightware.Application.Changes;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// The calendar area's state for the change stream (ADR 0007): every event calendar in the mailbox's scope with its
/// sync token (or, lacking one, its CTag), in href order, from the one calendar-home listing
/// <see cref="DavCalendarScopes"/> already makes. A calendar added, removed, or with any event changed moves it.
/// Calendars shared with the caller count too, since NightMail shows them.
/// </summary>
internal sealed class DavCalendarChangeProbe(DavCalendarScopes scopes) : IChangeStateProbe
{
    public ChangeArea Area => ChangeArea.Calendar;

    public async Task<Result<string>> StateAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        var state = new StringBuilder();
        foreach (var calendar in scope.Value.Calendars.Concat(scope.Value.SharedWithMe).OrderBy(static c => c.Href, StringComparer.Ordinal))
        {
            state.Append(CultureInfo.InvariantCulture, $"{calendar.Href}={calendar.SyncToken ?? calendar.CTag};");
        }

        return state.ToString();
    }
}
