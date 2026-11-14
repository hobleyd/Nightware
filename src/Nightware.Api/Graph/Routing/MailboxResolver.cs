using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.Routing;

/// <summary>
/// Resolves <c>/me</c> and <c>/users/{id|upn}</c> to the target mailbox.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>/me</c> → the caller's mail address.</item>
/// <item>An address/UPN (contains <c>@</c>, raw or <c>%40</c>-encoded) → itself, lower-cased.
/// Whether the caller may open it is the backend's decision (ADR 0001 §19).</item>
/// <item>Anything else is a directory id → <see cref="IUserDirectory.GetAsync"/>. While the directory
/// port is still the 501 stub, or when it does not know the id, the answer is 404.</item>
/// </list>
/// </remarks>
public sealed class MailboxResolver(IUserContext user, IUserDirectory directory) : IMailboxResolver
{
    public async Task<Result<MailboxRef>> ResolveAsync(string? userSegment, CancellationToken cancellationToken)
    {
        if (userSegment is null || string.Equals(userSegment, "me", StringComparison.OrdinalIgnoreCase))
        {
            var mail = user.Principal.Mail;
            return string.IsNullOrWhiteSpace(mail)
                ? Error.Forbidden("The signed-in user has no mailbox address (the access token carries no 'email' claim).")
                : new MailboxRef(mail.ToLowerInvariant());
        }

        var segment = Decode(userSegment).Trim();
        if (segment.Length == 0)
        {
            return Error.NotFound("No mailbox was named.");
        }

        if (segment.Contains('@', StringComparison.Ordinal))
        {
            return new MailboxRef(segment.ToLowerInvariant());
        }

        var found = await directory.GetAsync(segment, cancellationToken).ConfigureAwait(false);
        if (found.IsSuccess)
        {
            var address = found.Value.Mail ?? found.Value.UserPrincipalName;
            return string.IsNullOrWhiteSpace(address)
                ? Error.NotFound($"The user '{segment}' has no mailbox.")
                : new MailboxRef(address.ToLowerInvariant());
        }

        return found.Error.Kind is ErrorKind.NotFound or ErrorKind.Unsupported
            ? Error.NotFound($"The user '{segment}' was not found.")
            : found.Error;
    }

    private static string Decode(string segment)
    {
        if (!segment.Contains('%', StringComparison.Ordinal))
        {
            return segment;
        }

        try
        {
            return Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            return segment;
        }
    }
}
