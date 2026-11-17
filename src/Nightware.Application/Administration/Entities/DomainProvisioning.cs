using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>An entity with each of its domains as the mail server sees them.</summary>
public sealed record EntityDetail(Entity Entity, IReadOnlyList<DomainStatus> Domains);

/// <summary>The mail-server side of an entity's domains: status, and an idempotent "ensure it exists".</summary>
public sealed class DomainProvisioning(IMailDomains domains)
{
    public async Task<DomainStatus> StatusAsync(MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return ToStatus(domain, await domains.FindAsync(domain, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Creates the domain (with DKIM keys) on the mail server unless it is already there.</summary>
    public async Task<DomainStatus> EnsureAsync(Entity entity, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(domain);
        Result<MailDomainInfo> found = await domains.FindAsync(domain, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess && found.Error.Kind == ErrorKind.NotFound)
        {
            found = await domains.CreateAsync(domain, $"{entity.Name} ({entity.Alias})", cancellationToken).ConfigureAwait(false);
        }

        return ToStatus(domain, found);
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
            return new DomainStatus(domain, new BackendLink(BackendKind.Mail, LinkState.Linked, "Served by the mail server.", found.Value.Id), found.Value.DkimPresent);
        }

        BackendLink link = found.Error.Kind == ErrorKind.NotFound
            ? new BackendLink(BackendKind.Mail, LinkState.Missing, "The mail server does not serve this domain yet.")
            : new BackendLink(BackendKind.Mail, LinkState.Error, found.Error.Message);
        return new DomainStatus(domain, link, DkimPresent: false);
    }
}
