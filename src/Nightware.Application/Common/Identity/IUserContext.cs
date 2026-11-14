using Nightware.Domain.Kernel;

namespace Nightware.Application.Common.Identity;

/// <summary>
/// The authenticated caller of the current request. Adapters must not read
/// <see cref="BearerToken"/> directly — they obtain backend credentials through
/// <see cref="IBackendCredentials"/>.
/// </summary>
public interface IUserContext
{
    Principal Principal { get; }

    /// <summary>The inbound access token, used only by <see cref="IBackendCredentials"/> strategies.</summary>
    string BearerToken { get; }
}

/// <summary>Resolves the <c>/me</c> or <c>/users/{id|upn}</c> path segment to the target mailbox.</summary>
public interface IMailboxResolver
{
    /// <param name="userSegment">Null for <c>/me</c>; otherwise the raw <c>{id|upn}</c> segment.</param>
    Task<Result<MailboxRef>> ResolveAsync(string? userSegment, CancellationToken cancellationToken);
}
