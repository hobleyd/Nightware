using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>
/// Stalwart shared mailboxes (phase 10): <c>x:Account</c> objects of type <c>Group</c>, managed with the console's own
/// principal. An account opens a group's mailbox while the group's id is in its <c>memberGroupIds</c>: the group then
/// appears in the member's JMAP session as a shared account named by its address, with its own folders and identity,
/// and the member's own account gets an identity sending as it. Taking the id out removes both at once (step 0,
/// verified on 1.1). Membership is patched one id at a time, so a member's other shared mailboxes are left alone.
/// </summary>
internal sealed partial class StalwartSharedMailboxes(
    IStalwartManagementClient client,
    IOptions<StalwartManagementOptions> options,
    ILogger<StalwartSharedMailboxes> logger) : ISharedMailAccounts
{
    private static readonly string[] MailboxProperties = ["@type", "name", "domainId", "emailAddress", "description", "aliases", "roles", "permissions"];

    public async Task<Result<SharedMailAccount>> FindByAddressAsync(MailAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        Result<StalwartDomain> domain = await StalwartMailDomains.FindDomainAsync(client, address.Domain.Name, ["name"], cancellationToken).ConfigureAwait(false);
        if (!domain.IsSuccess)
        {
            return domain.Error.Kind == ErrorKind.NotFound ? NotFound(address) : domain.Error;
        }

        // The name + domainId filter finds group accounts as well as users (step 0); confirm the address and the type.
        var request = new JmapRequestBuilder();
        var query = request.Add(StalwartMethods.AccountQuery, new StalwartQueryRequest<AccountFilter>
        {
            Filter = new AccountFilter { Name = address.LocalPart, DomainId = domain.Value.Id },
        });
        var get = request.Add(StalwartMethods.AccountGet, new StalwartGetRequest { Properties = MailboxProperties }, query.Ref("ids", JmapPaths.Ids));
        Result<GetResponse<StalwartAccount>> accounts = (await client.SendAsync(request, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(get));
        if (!accounts.IsSuccess)
        {
            return accounts.Error;
        }

        StalwartAccount? match = accounts.Value.List.FirstOrDefault(a => a.IsGroup && address.Matches(a.EmailAddress));
        if (match is null)
        {
            return NotFound(address);
        }

        Result<IReadOnlyList<SharedMailMember>> members = await MembersAsync(match.Id, cancellationToken).ConfigureAwait(false);
        if (!members.IsSuccess)
        {
            return members.Error;
        }

        Result<IReadOnlyList<string>> aliases = await AliasesAsync(match, domain.Value, cancellationToken).ConfigureAwait(false);
        if (!aliases.IsSuccess)
        {
            return aliases.Error;
        }

        return new SharedMailAccount(match.Id, match.EmailAddress ?? address.Value)
        {
            Name = match.Description,
            Aliases = aliases.Value,
            Members = members.Value,
            IsPrivileged = match.IsPrivileged,
        };
    }

    public async Task<Result<SharedMailAccount>> CreateAsync(MailAddress address, IReadOnlyList<MailAddress> aliases, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Result<Dictionary<string, string>> domainIds = await DomainIdsAsync(aliases.Prepend(address), cancellationToken).ConfigureAwait(false);
        if (!domainIds.IsSuccess)
        {
            return domainIds.Error;
        }

        // The description names the identity Stalwart makes for the mailbox, so it is the mailbox's name.
        var account = new JsonObject
        {
            ["@type"] = "Group",
            ["name"] = address.LocalPart,
            ["domainId"] = domainIds.Value[address.Domain.Name],
            ["description"] = name,
            ["aliases"] = Aliases(aliases, domainIds.Value),
        };
        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.AccountSet, new StalwartSetRequest { Create = new Dictionary<string, JsonObject> { ["g"] = account } });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Result<StalwartCreated> created = response.Bind(r => r.Get(set)).Bind(static r =>
            r.NotCreated?.TryGetValue("g", out SetError? refused) == true ? StalwartMethods.FromSetError(refused, "g") : r.Created("g"));
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        LogCreated(logger, created.Value.Id);
        return new SharedMailAccount(created.Value.Id, address.Value) { Name = name, Aliases = [.. aliases.Select(static a => a.Value)] };
    }

    public async Task<Result> UpdateAsync(string mailboxId, IReadOnlyList<MailAddress> aliases, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailboxId);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Result<Dictionary<string, string>> domainIds = await DomainIdsAsync(aliases, cancellationToken).ConfigureAwait(false);
        if (!domainIds.IsSuccess)
        {
            return domainIds.Error;
        }

        Result updated = await UpdateAccountAsync(mailboxId, new JsonObject { ["aliases"] = Aliases(aliases, domainIds.Value), ["description"] = name }, cancellationToken).ConfigureAwait(false);
        if (updated.IsSuccess)
        {
            LogUpdated(logger, mailboxId, aliases.Count);
        }

        return updated;
    }

    public async Task<Result> AddMemberAsync(string mailboxId, string memberAccountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberAccountId);
        Result added = await UpdateAccountAsync(memberAccountId, new JsonObject { [$"memberGroupIds/{mailboxId}"] = true }, cancellationToken).ConfigureAwait(false);
        if (added.IsSuccess)
        {
            LogMemberAdded(logger, memberAccountId, mailboxId);
        }

        return added;
    }

    public async Task<Result> RemoveMemberAsync(string mailboxId, string memberAccountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberAccountId);
        Result removed = await UpdateAccountAsync(memberAccountId, new JsonObject { [$"memberGroupIds/{mailboxId}"] = null }, cancellationToken).ConfigureAwait(false);
        if (removed.IsSuccess)
        {
            LogMemberRemoved(logger, memberAccountId, mailboxId);
        }

        return removed;
    }

    public async Task<Result> DestroyAsync(string mailboxId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailboxId);
        var read = new JmapRequestBuilder();
        var get = read.Add(StalwartMethods.AccountGet, new StalwartGetRequest { Ids = [mailboxId], Properties = ["@type", "roles", "permissions"] });
        Result<GetResponse<StalwartAccount>> found = (await client.SendAsync(read, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(get));
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        // Only ever a group account: a person's mailbox is never destroyed through here.
        StalwartAccount? account = found.Value.List.FirstOrDefault(a => a.Id == mailboxId);
        if (account is null)
        {
            return Error.NotFound($"The mail server has no shared mailbox {mailboxId}.", mailboxId);
        }

        if (!account.IsGroup)
        {
            return Error.Conflict($"The mail server's account {mailboxId} is not a shared mailbox.", mailboxId);
        }

        // Stalwart refuses to destroy a group its members still link to (objectIsLinked), members added by hand included.
        Result<IReadOnlyList<SharedMailMember>> members = await MembersAsync(mailboxId, cancellationToken).ConfigureAwait(false);
        if (!members.IsSuccess)
        {
            return members.Error;
        }

        var request = new JmapRequestBuilder();
        JmapCall<SetResponse<StalwartCreated>>? unlink = members.Value.Count == 0 ? null : request.Add(StalwartMethods.AccountSet, new StalwartSetRequest
        {
            Update = members.Value.ToDictionary(static m => m.AccountId, m => new JsonObject { [$"memberGroupIds/{mailboxId}"] = null }, StringComparer.Ordinal),
        });
        var destroy = request.Add(StalwartMethods.AccountSet, new StalwartSetRequest { Destroy = [mailboxId] });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        Result unlinked = unlink is null ? Result.Success() : response.Value.Get(unlink).Match(static r => r.AllSucceeded(), static e => Result.Failure(e));
        Result destroyed = response.Value.Get(destroy).Match(r => r.Destroyed(mailboxId), static e => Result.Failure(e));
        if (!unlinked.IsSuccess)
        {
            return unlinked;
        }

        if (destroyed.IsSuccess)
        {
            LogDestroyed(logger, mailboxId, members.Value.Count);
        }

        return destroyed;
    }

    private static Error NotFound(MailAddress address) => Error.NotFound($"No shared mailbox has the address {address}.", address.Value);

    /// <summary>As on users: a map keyed by index (an array is refused with <c>invalidPatch</c>).</summary>
    private static JsonObject Aliases(IReadOnlyList<MailAddress> aliases, Dictionary<string, string> domainIds)
    {
        var list = new JsonObject();
        for (int i = 0; i < aliases.Count; i++)
        {
            list[i.ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["name"] = aliases[i].LocalPart, ["domainId"] = domainIds[aliases[i].Domain.Name] };
        }

        return list;
    }

    private async Task<Result> UpdateAccountAsync(string accountId, JsonObject patch, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.AccountSet, new StalwartSetRequest { Update = new Dictionary<string, JsonObject> { [accountId] = patch } });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Match(
            r => r.NotUpdated?.TryGetValue(accountId, out SetError? refused) == true ? StalwartMethods.FromSetError(refused, accountId) : r.Updated(accountId),
            static e => Result.Failure(e));
    }

    /// <summary>The accounts whose <c>memberGroupIds</c> holds the group, page by page, bounded like the alias scan.</summary>
    private async Task<Result<IReadOnlyList<SharedMailMember>>> MembersAsync(string groupId, CancellationToken cancellationToken)
    {
        StalwartManagementOptions settings = options.Value;
        var members = new List<SharedMailMember>();
        int position = 0;
        while (position < settings.MaxAccountsScanned)
        {
            int limit = Math.Min(settings.AccountPageSize, settings.MaxAccountsScanned - position);
            var request = new JmapRequestBuilder();
            var query = request.Add(StalwartMethods.AccountQuery, new StalwartQueryRequest<AccountFilter>
            {
                Filter = new AccountFilter { MemberGroupIds = groupId },
                Position = position,
                Limit = limit,
            });
            var get = request.Add(StalwartMethods.AccountGet, new StalwartGetRequest { Properties = ["emailAddress"] }, query.Ref("ids", JmapPaths.Ids));
            Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Result<(int Ids, int? ServerLimit, IReadOnlyList<StalwartAccount> Accounts)> page = response.Bind(r => r.Get(query).Bind(q => r.Get(get).Map(g => (q.Ids.Count, q.Limit, g.List))));
            if (!page.IsSuccess)
            {
                return page.Error;
            }

            members.AddRange(page.Value.Accounts.Where(static a => a.EmailAddress is not null).Select(static a => new SharedMailMember(a.Id, a.EmailAddress!)));

            // As in the alias scan: only a page shorter than the effective limit (or empty) is the last one.
            if (page.Value.Ids == 0 || page.Value.Ids < (page.Value.ServerLimit ?? limit))
            {
                return members;
            }

            position += page.Value.Ids;
        }

        return JmapErrors.Unexpected($"Could not list the members of shared mailbox {groupId}: more than {settings.MaxAccountsScanned} (StalwartManagement:MaxAccountsScanned).");
    }

    private async Task<Result<IReadOnlyList<string>>> AliasesAsync(StalwartAccount account, StalwartDomain lookedUp, CancellationToken cancellationToken)
    {
        IReadOnlyList<StalwartAlias> aliases = StalwartMethods.ReadAliases(account.Aliases);
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { [lookedUp.Id] = lookedUp.Name ?? string.Empty };
        string[] unknown = [.. aliases.Select(static a => a.DomainId).Distinct(StringComparer.Ordinal).Where(id => !names.ContainsKey(id))];
        if (unknown.Length > 0)
        {
            var request = new JmapRequestBuilder();
            var get = request.Add(StalwartMethods.DomainGet, new StalwartGetRequest { Ids = unknown, Properties = ["name"] });
            Result<GetResponse<StalwartDomain>> domains = (await client.SendAsync(request, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(get));
            if (!domains.IsSuccess)
            {
                return domains.Error;
            }

            foreach (StalwartDomain domain in domains.Value.List.Where(static d => d.Name is not null))
            {
                names[domain.Id] = domain.Name!;
            }
        }

        return aliases.Where(a => names.ContainsKey(a.DomainId)).Select(a => $"{a.Name}@{names[a.DomainId]}").ToList();
    }

    private async Task<Result<Dictionary<string, string>>> DomainIdsAsync(IEnumerable<MailAddress> addresses, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in addresses.Select(static a => a.Domain.Name).Distinct(StringComparer.Ordinal))
        {
            Result<StalwartDomain> domain = await StalwartMailDomains.FindDomainAsync(client, name, ["name"], cancellationToken).ConfigureAwait(false);
            if (!domain.IsSuccess)
            {
                return domain.Error.Kind == ErrorKind.NotFound
                    ? Error.Validation($"The mail server does not serve {name}; add the domain to the entity first.", "domain")
                    : domain.Error;
            }

            ids[name] = domain.Value.Id;
        }

        return ids;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created Stalwart shared mailbox {MailboxId}")]
    private static partial void LogCreated(ILogger logger, string mailboxId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Set {Count} alias(es) and the name on Stalwart shared mailbox {MailboxId}")]
    private static partial void LogUpdated(ILogger logger, string mailboxId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stalwart account {AccountId} may now open shared mailbox {MailboxId}")]
    private static partial void LogMemberAdded(ILogger logger, string accountId, string mailboxId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stalwart account {AccountId} may no longer open shared mailbox {MailboxId}")]
    private static partial void LogMemberRemoved(ILogger logger, string accountId, string mailboxId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Destroyed Stalwart shared mailbox {MailboxId} after taking out {Count} member(s)")]
    private static partial void LogDestroyed(ILogger logger, string mailboxId, int count);
}
