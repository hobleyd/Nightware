using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>An entity with each of its domains as the mail server sees them.</summary>
public sealed record EntityDetail(Entity Entity, IReadOnlyList<DomainStatus> Domains);

/// <summary>The mail-server side of an entity's domains: status, and an idempotent "ensure it exists".</summary>
public sealed class DomainProvisioning(IMailDomains domains, IIdentityAccounts identities)
{
    public async Task<DomainStatus> StatusAsync(MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return ToStatus(domain, await domains.FindAsync(domain, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Creates the domain (with DKIM keys) on the mail server unless it is already there. A domain that is there
    /// without DKIM keys (created, but the reload that loads its keys failed) gets that reload now. A catch-all the
    /// entity records is sent again when the mail server's differs (a domain created anew has none). One the mail
    /// server has without a record (set outside the console) is left alone.
    /// </summary>
    public async Task<DomainStatus> EnsureAsync(Entity entity, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(domain);
        Result<MailDomainInfo> found = await domains.FindAsync(domain, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess && found.Error.Kind == ErrorKind.NotFound)
        {
            found = await domains.CreateAsync(domain, $"{entity.Name} ({entity.Alias})", cancellationToken).ConfigureAwait(false);
        }
        else if (found.IsSuccess && !found.Value.DkimPresent)
        {
            Result reloaded = await domains.ReloadSettingsAsync(cancellationToken).ConfigureAwait(false);
            found = reloaded.IsSuccess
                ? await domains.FindAsync(domain, cancellationToken).ConfigureAwait(false)
                : Error.BackendUnavailable($"The mail server serves {domain.Name} without DKIM keys and did not reload its settings ({reloaded.Error!.Message}).");
        }

        if (found.IsSuccess && entity.CatchAllFor(domain) is { } userId)
        {
            found = found.Value with { CatchAllAddress = await RestoreCatchAllAsync(domain, userId, found.Value.CatchAllAddress, cancellationToken).ConfigureAwait(false) };
        }

        return ToStatus(domain, found);
    }

    /// <summary>The mail server's catch-all after sending the recorded user's address, when it differs and can be found.</summary>
    private async Task<string?> RestoreCatchAllAsync(MailDomain domain, string userId, string? current, CancellationToken cancellationToken)
    {
        Result<IdentityAccount> user = await identities.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess
            || string.Equals(current, user.Value.Email, StringComparison.OrdinalIgnoreCase)
            || !MailAddress.TryParse(user.Value.Email, out MailAddress? address))
        {
            return current;
        }

        Result set = await domains.SetCatchAllAsync(domain, address, cancellationToken).ConfigureAwait(false);
        return set.IsSuccess ? address.Value : current;
    }

    public async Task<EntityDetail> DetailAsync(Entity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        DomainStatus[] statuses = await Task.WhenAll(entity.Domains.Select(d => StatusAsync(d, cancellationToken))).ConfigureAwait(false);
        return new EntityDetail(entity, statuses);
    }

    private static DomainStatus ToStatus(MailDomain domain, Result<MailDomainInfo> found)
    {
        if (found.IsSuccess)
        {
            return new DomainStatus(domain, new BackendLink(BackendKind.Mail, LinkState.Linked, "Served by the mail server.", found.Value.Id), found.Value.DkimPresent)
            {
                CatchAllAddress = found.Value.CatchAllAddress,
            };
        }

        BackendLink link = found.Error.Kind == ErrorKind.NotFound
            ? new BackendLink(BackendKind.Mail, LinkState.Missing, "The mail server does not serve this domain yet.")
            : new BackendLink(BackendKind.Mail, LinkState.Error, found.Error.Message);
        return new DomainStatus(domain, link, DkimPresent: false);
    }
}
