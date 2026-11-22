using Nightware.Domain.Kernel;

namespace Nightware.Application.Changes;

/// <summary>What a change signal is about. Each value is one word on the wire (ADR 0007).</summary>
public enum ChangeArea
{
    /// <summary>Any message or folder of the mailbox: new mail, a read, a move, a folder count.</summary>
    Mail,

    /// <summary>Any event of the mailbox's calendars.</summary>
    Calendar,
}

/// <summary>
/// Reads one area's backend state as an opaque string that changes whenever anything in the area changes (a JMAP
/// state, the calendars' sync tokens). Cheap enough to read every few seconds; never lists items. Runs as the
/// caller of the current request.
/// </summary>
public interface IChangeStateProbe
{
    ChangeArea Area { get; }

    /// <returns>
    /// The state; <see cref="ErrorKind.NotFound"/> or <see cref="ErrorKind.Unsupported"/> when the mailbox has
    /// nothing in this area (a shared mailbox's contacts), <see cref="ErrorKind.BackendAuthFailed"/> or
    /// <see cref="ErrorKind.Unauthorized"/> when the caller's credential no longer works.
    /// </returns>
    Task<Result<string>> StateAsync(MailboxRef mailbox, CancellationToken cancellationToken);
}
