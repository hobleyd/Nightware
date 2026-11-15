using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Calendar;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// <see cref="IFreeBusyReader"/> through RFC 6638 free-busy: one VFREEBUSY REQUEST to the caller's own
/// scheduling outbox for every address (people and rooms alike). Sabre only accepts a POST to the
/// caller's outbox with the caller as ORGANIZER, so <c>/users/{x}/calendar/getSchedule</c> asks as the caller too.
/// </summary>
internal sealed class DavFreeBusyReader(IDavPrincipalDiscovery discovery, IDavClient dav, FreeBusyCodec codec) : IFreeBusyReader
{
    public async Task<Result<IReadOnlyList<FreeBusySchedule>>> GetAsync(MailboxRef mailbox, IReadOnlyList<string> addresses, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            return Result<IReadOnlyList<FreeBusySchedule>>.Success([]);
        }

        var self = await discovery.CurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!self.IsSuccess)
        {
            return self.Error;
        }

        if (self.Value.ScheduleOutboxUrl is not { } outbox)
        {
            return Error.Forbidden("Your calendar account cannot query free/busy information.");
        }

        var organizer = self.Value.EmailAddresses.FirstOrDefault(a => string.Equals(a, mailbox.Address, StringComparison.OrdinalIgnoreCase))
                        ?? (self.Value.EmailAddresses.Count > 0 ? self.Value.EmailAddresses[0] : mailbox.Address);
        var replies = await dav.PostToOutboxAsync(outbox, codec.BuildRequest(organizer, addresses, start, end), cancellationToken).ConfigureAwait(false);
        if (!replies.IsSuccess)
        {
            return replies.Error;
        }

        IReadOnlyList<FreeBusySchedule> schedules = replies.Value.Select(ToSchedule).ToList();
        return Result<IReadOnlyList<FreeBusySchedule>>.Success(schedules);
    }

    private static FreeBusySchedule ToSchedule(DavScheduleResponseItem reply)
    {
        var address = reply.Recipient.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(reply.Recipient["mailto:".Length..])
            : reply.Recipient;
        return reply.IsSuccess
            ? new FreeBusySchedule(address, FreeBusyCodec.ParseReply(reply.CalendarData))
            : new FreeBusySchedule(address, []) { ErrorMessage = $"Free/busy information is not available for {address} ({reply.RequestStatus})." };
    }
}
