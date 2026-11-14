using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Mail.Ids;

/// <summary>
/// Resolves a Graph <see cref="MessageId"/> to the JMAP email id it currently denotes. <c>e.</c> ids
/// decode directly; <c>d.</c> (draft) ids are looked up by Message-ID in the Drafts mailbox and
/// confirmed with <see cref="MailItemId.MatchesDraft"/> (ADR 0001 §1).
/// Implemented once, by Phase 2 (registered from <c>Mail/Read</c>); Phase 3 consumes it.
/// </summary>
internal interface IJmapMessageLocator
{
    /// <returns>The JMAP id, <see cref="ErrorKind.NotFound"/> when no message matches, or
    /// <see cref="ErrorKind.Validation"/> for a malformed id.</returns>
    Task<Result<string>> ResolveAsync(JmapAccount account, MessageId id, CancellationToken cancellationToken);
}
