using System.Security.Cryptography;
using System.Text;

using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// <see cref="IInvitationImporter"/> over CalDAV: the invitation, made storable (<see cref="IcsEditor.ForImport"/>), is
/// PUT into the mailbox's default calendar under a name derived from its UID with <c>If-None-Match: *</c>, so a second
/// import (or a race) finds it there and changes nothing.
/// </summary>
internal sealed class DavInvitationImporter(DavCalendarScopes scopes, IDavClient dav, IDavPrincipalDiscovery discovery) : IInvitationImporter
{
    public async Task<Result> ImportAsync(MailboxRef mailbox, string ics, CancellationToken cancellationToken)
    {
        var loaded = IcalNetCalendarCodec.Load(ics);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var meeting = loaded.Value.Events.FirstOrDefault(static e => !string.IsNullOrWhiteSpace(e.Uid));
        var organizer = meeting?.Organizer is { } o ? IcalNetCalendarCodec.AddressOf(o.Value) : null;
        if (meeting is null || string.IsNullOrEmpty(organizer))
        {
            return Error.Validation("The invitation names no meeting or organizer.");
        }

        // Nextcloud delivers its own users' invitations; one that is missing was never meant for this calendar.
        if (await discovery.DiscoverAsync(new MailboxRef(organizer), cancellationToken).ConfigureAwait(false) is not { IsSuccess: false, Error.Kind: ErrorKind.NotFound })
        {
            return Error.NotFound("The organizer is on this server; its invitations are delivered, not imported.");
        }

        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        if (scope.Value.Default is not { } calendar)
        {
            return Error.Forbidden($"{mailbox.Address} has no calendar to put the invitation in.");
        }

        var storable = IcsEditor.ForImport(ics);
        if (!storable.IsSuccess)
        {
            return storable.Error;
        }

        var put = await dav.PutAsync(calendar.Href + ResourceName(meeting.Uid!), storable.Value, DavEventResources.ContentType, DavPrecondition.CreateOnly, cancellationToken)
            .ConfigureAwait(false);

        // Already there (an earlier import, or Sabre's no-uid-conflict for the same UID): nothing to do.
        return put.IsSuccess || put.Error.Kind == ErrorKind.Conflict ? Result.Success() : put.Error;
    }

    /// <summary>A resource name from the UID (never the UID itself, which may hold anything): <c>imported-{sha256}.ics</c>.</summary>
    internal static string ResourceName(string uid) =>
        "imported-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uid)))[..32] + ".ics";
}
