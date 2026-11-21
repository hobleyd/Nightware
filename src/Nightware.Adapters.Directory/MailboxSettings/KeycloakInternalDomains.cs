using Microsoft.Extensions.Logging;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.MailboxSettings;

/// <summary>
/// <see cref="IInternalDomains"/> from Keycloak Organizations: the entity whose domains include the mailbox's
/// (a domain belongs to at most one), read with the façade's service account (<c>view-organizations</c>).
/// When there is none, or Keycloak can't be asked, only the mailbox's own domain is internal.
/// </summary>
internal sealed partial class KeycloakInternalDomains(IKeycloakAdminClient keycloak, ILogger<KeycloakInternalDomains> logger) : IInternalDomains
{
    public async Task<IReadOnlyList<string>> ForAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var at = mailbox.Address.LastIndexOf('@');
        var own = at < 0 ? mailbox.Address.ToLowerInvariant() : mailbox.Address[(at + 1)..].ToLowerInvariant();
        var found = await keycloak.SearchOrganizationsAsync(own, exact: true, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            LogLookupFailed(logger, own, found.Error.Message);
            return [own];
        }

        var entity = found.Value.FirstOrDefault(o => o.Domains?.Any(d => string.Equals(d.Name, own, StringComparison.OrdinalIgnoreCase)) == true);
        return [.. (entity?.Domains ?? []).Select(static d => d.Name.ToLowerInvariant()).Append(own).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the entity of {Domain} for automatic replies; only it counts as internal: {Reason}")]
    private static partial void LogLookupFailed(ILogger logger, string domain, string reason);
}
