using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.SharedMailboxes;

/// <summary>
/// What an admin asks a shared mailbox to be (phase 10). <see cref="LocalPart"/> and <see cref="Domain"/> are only
/// read when the mailbox is created: its address never changes.
/// </summary>
public sealed record SharedMailboxForm(string Name)
{
    public string? LocalPart { get; init; }

    public string? Domain { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>A shared mailbox with its entity, the people who may open it and its state in each backend.</summary>
public sealed record SharedMailboxDetail(Entity Entity, SharedMailbox Mailbox, IReadOnlyList<IdentityAccount> Members, IReadOnlyList<BackendLink> Links)
{
    public BackendLink? For(BackendKind backend) => Links.SingleOrDefault(l => l.Backend == backend);
}

/// <summary>
/// Loads an entity's shared mailboxes for a use case behind the <see cref="AdminAuthorization.RequireEntity"/> check,
/// and reads <see cref="SharedMailboxForm"/>s against the entity's domains and the addresses already in use. Every
/// shared mailbox use case goes through here, so none can forget the check.
/// </summary>
public sealed class SharedMailboxAccess(
    IAdminActor actor,
    IEntityRegistry entities,
    IEntityMailboxes mailboxes,
    IIdentityAccounts identities,
    IMailAccounts mail,
    IMailingLists lists,
    SharedMailboxSync sync,
    AdministrationOptions options)
{
    public const int MaxNameLength = 80;

    public async Task<Result<Entity>> LoadEntityAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result allowed = AdminAuthorization.RequireEntity(actor.Current, entityId);
        return allowed.IsSuccess ? await entities.GetAsync(entityId, cancellationToken).ConfigureAwait(false) : allowed.Error;
    }

    public async Task<Result<(Entity Entity, SharedMailbox Mailbox)>> LoadAsync(string entityId, string mailboxId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailboxId);
        Result<Entity> entity = await LoadEntityAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        Result<SharedMailbox> mailbox = await mailboxes.GetAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        return mailbox.IsSuccess ? (entity.Value, mailbox.Value) : mailbox.Error;
    }

    public async Task<Result<SharedMailboxDetail>> LoadDetailAsync(string entityId, string mailboxId, CancellationToken cancellationToken)
    {
        Result<(Entity Entity, SharedMailbox Mailbox)> loaded = await LoadAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result<IReadOnlyList<IdentityAccount>> members = await mailboxes.ListMembersAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        if (!members.IsSuccess)
        {
            return members.Error;
        }

        IReadOnlyList<BackendLink> links = await sync.InspectAsync(loaded.Value.Mailbox, members.Value, cancellationToken).ConfigureAwait(false);
        return new SharedMailboxDetail(loaded.Value.Entity, loaded.Value.Mailbox, members.Value, links);
    }

    /// <summary>
    /// The name, address and aliases <paramref name="form"/> asks for. <paramref name="existing"/> is the mailbox being
    /// changed (null for a new one): its address is kept, and its own addresses are not "in use".
    /// </summary>
    public async Task<Result<(string Name, MailAddress Address, IReadOnlyList<MailAddress> Aliases)>> ReadAsync(
        Entity entity, SharedMailboxForm form, SharedMailbox? existing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(form);
        string name = form.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return Error.Validation($"Give the shared mailbox a name of at most {MaxNameLength} characters.", "name");
        }

        MailAddress? address = existing?.Address;
        if (address is null && (!MailDomain.TryParse(form.Domain, out MailDomain? domain) || !MailAddress.TryCreate(form.LocalPart, domain, out address)))
        {
            return Error.Validation("Enter the shared mailbox's address: letters, digits, dots, hyphens or underscores before the @, on one of the entity's domains.", "address");
        }

        var aliases = new List<MailAddress>();
        foreach (string raw in form.Aliases.Where(static a => !string.IsNullOrWhiteSpace(a)))
        {
            if (!MailAddress.TryParse(raw, out MailAddress? alias))
            {
                return Error.Validation($"'{raw.Trim()}' is not an address the console can give a shared mailbox.", "aliases");
            }

            if (alias != address && !aliases.Contains(alias))
            {
                aliases.Add(alias);
            }
        }

        IEnumerable<MailAddress> owned = existing?.Addresses ?? [];
        foreach (MailAddress added in aliases.Prepend(address!).Where(a => !owned.Contains(a)))
        {
            Result valid = AdminAuthorization.RequireAddressInEntity(entity, added);
            if (valid.IsSuccess)
            {
                valid = AdminAuthorization.RequireNotReserved(added, options);
            }

            if (valid.IsSuccess)
            {
                valid = await AddressAvailability.RequireFreeAsync(identities, lists, mail, added, cancellationToken).ConfigureAwait(false);
            }

            if (!valid.IsSuccess)
            {
                return valid.Error;
            }
        }

        return (name, address!, aliases);
    }
}
