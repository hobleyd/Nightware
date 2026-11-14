using Nightware.Domain.Kernel;
using Nightware.Domain.Security;

namespace Nightware.Application.Security;

/// <summary>Reports a message as spam/phishing: files it as junk and trains the spam classifier.</summary>
public interface IThreatReporter
{
    Task<Result<ThreatSubmission>> ReportAsync(MailboxRef mailbox, MessageId id, ThreatCategory category, CancellationToken cancellationToken);
}
