using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// Stands in for <see cref="IJmapMessageLocator"/> until Mail/Read (Phase 2) registers the real one, so
/// the container validates on a branch without it. It is registered with <c>TryAdd</c> after Mail/Read,
/// so the real locator always wins; it resolves nothing itself (ADR 0001 §41: one resolver, Phase 2's).
/// </summary>
internal sealed class UnavailableMessageLocator : IJmapMessageLocator
{
    public Task<Result<string>> ResolveAsync(JmapAccount account, MessageId id, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>(Error.Unsupported("Resolving message ids is not available."));
}
