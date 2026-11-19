using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Groups;

/// <summary>
/// What an admin asks a group to be (phase 9). <see cref="LocalPart"/> and <see cref="Domain"/> name the list's
/// address when the mail facet is turned on; once a group has an address it never changes.
/// </summary>
public sealed record GroupForm(string Name)
{
    /// <summary>Only read when the group is created; proposed from the name when empty.</summary>
    public string? Key { get; init; }

    public bool Mail { get; init; }

    public string? LocalPart { get; init; }

    public string? Domain { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public SenderPolicy Senders { get; init; } = SenderPolicy.Members;

    public IReadOnlyList<string> ExternalRecipients { get; init; } = [];

    public bool Files { get; init; }

    public bool TeamFolder { get; init; }
}

/// <summary>
/// Loads an entity's groups for a use case behind the <see cref="AdminAuthorization.RequireEntity"/> check, and reads
/// <see cref="GroupForm"/>s against the entity's domains and the addresses already in use. Every group use case goes
/// through here, so none can forget the check.
/// </summary>
public sealed class GroupAccess(
    IAdminActor actor,
    IEntityRegistry entities,
    IEntityGroups groups,
    IIdentityAccounts identities,
    IMailAccounts mail,
    IMailingLists lists,
    GroupSync sync,
    AdministrationOptions options)
{
    public const int MaxNameLength = 80;

    public async Task<Result<Entity>> LoadEntityAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result allowed = AdminAuthorization.RequireEntity(actor.Current, entityId);
        return allowed.IsSuccess ? await entities.GetAsync(entityId, cancellationToken).ConfigureAwait(false) : allowed.Error;
    }

    public async Task<Result<(Entity Entity, Group Group)>> LoadAsync(string entityId, string groupId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        Result<Entity> entity = await LoadEntityAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        Result<Group> group = await groups.GetAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        return group.IsSuccess ? (entity.Value, group.Value) : group.Error;
    }

    public async Task<Result<GroupDetail>> LoadDetailAsync(string entityId, string groupId, CancellationToken cancellationToken)
    {
        Result<(Entity Entity, Group Group)> loaded = await LoadAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result<IReadOnlyList<IdentityAccount>> members = await groups.ListMembersAsync(entityId, groupId, cancellationToken).ConfigureAwait(false);
        if (!members.IsSuccess)
        {
            return members.Error;
        }

        IReadOnlyList<BackendLink> links = await sync.InspectAsync(loaded.Value.Entity, loaded.Value.Group, members.Value, cancellationToken).ConfigureAwait(false);
        return new GroupDetail(loaded.Value.Entity, loaded.Value.Group, members.Value, links);
    }

    /// <summary>
    /// The facets <paramref name="form"/> asks for. <paramref name="existing"/> is the group being changed (null for a
    /// new group): its address is kept, and its own addresses are not "in use".
    /// </summary>
    public async Task<Result<(string Name, GroupMail? Mail, GroupFiles? Files)>> ReadAsync(Entity entity, GroupForm form, Group? existing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(form);
        string name = form.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return Error.Validation($"Give the group a name of at most {MaxNameLength} characters.", "name");
        }

        GroupMail? groupMail = null;
        if (form.Mail)
        {
            Result<GroupMail> read = await ReadMailAsync(entity, form, existing?.Mail, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return read.Error;
            }

            groupMail = read.Value;
        }

        GroupFiles? files = null;
        if (form.Files)
        {
            files = new GroupFiles(form.TeamFolder) { TeamFolderId = form.TeamFolder ? existing?.Files?.TeamFolderId : null };
        }

        // A team folder is only taken away by deleting it, so its files are never left shared with no one by accident.
        if (existing?.Files is { TeamFolder: true } && files is not { TeamFolder: true })
        {
            return Error.Validation("This group has a team folder. Delete the team folder first if you no longer want it.", "teamFolder");
        }

        return (name, groupMail, files);
    }

    private async Task<Result<GroupMail>> ReadMailAsync(Entity entity, GroupForm form, GroupMail? existing, CancellationToken cancellationToken)
    {
        MailAddress? address = existing?.Address;
        if (address is null)
        {
            if (!MailDomain.TryParse(form.Domain, out MailDomain? domain) || !MailAddress.TryCreate(form.LocalPart, domain, out address))
            {
                return Error.Validation("Enter the list's address: letters, digits, dots, hyphens or underscores before the @, on one of the entity's domains.", "address");
            }
        }

        var aliases = new List<MailAddress>();
        foreach (string raw in form.Aliases.Where(static a => !string.IsNullOrWhiteSpace(a)))
        {
            if (!MailAddress.TryParse(raw, out MailAddress? alias))
            {
                return Error.Validation($"'{raw.Trim()}' is not an address the console can give a list.", "aliases");
            }

            if (alias != address && !aliases.Contains(alias))
            {
                aliases.Add(alias);
            }
        }

        var externals = new List<RecipientAddress>();
        foreach (string raw in form.ExternalRecipients.Where(static a => !string.IsNullOrWhiteSpace(a)))
        {
            if (!RecipientAddress.TryParse(raw, out RecipientAddress? recipient))
            {
                return Error.Validation($"'{raw.Trim()}' is not an e-mail address.", "externalRecipients");
            }

            if (MailDomain.TryParse(recipient.Value[(recipient.Value.LastIndexOf('@') + 1)..], out MailDomain? domain) && entity.Domains.Contains(domain))
            {
                return Error.Validation($"{recipient} is on one of {entity.Name}'s own domains: add that person as a member instead.", "externalRecipients");
            }

            if (!externals.Contains(recipient))
            {
                externals.Add(recipient);
            }
        }

        var groupMail = new GroupMail(address, form.Senders) { Aliases = aliases, ExternalRecipients = externals };
        Result valid = groupMail.Validate();
        IEnumerable<MailAddress> owned = existing?.Addresses ?? [];
        foreach (MailAddress added in groupMail.Addresses.Where(a => !owned.Contains(a)))
        {
            if (!valid.IsSuccess)
            {
                break;
            }

            valid = AdminAuthorization.RequireAddressInEntity(entity, added);
            if (valid.IsSuccess)
            {
                valid = AdminAuthorization.RequireNotReserved(added, options);
            }

            if (valid.IsSuccess)
            {
                valid = await AddressAvailability.RequireFreeAsync(identities, lists, mail, added, cancellationToken).ConfigureAwait(false);
            }
        }

        return valid.IsSuccess ? groupMail : valid.Error;
    }
}
