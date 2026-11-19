using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>
/// Stalwart distribution lists (<c>x:MailingList</c>) and the system Sieve script that limits who may send to them
/// (phase 9), managed with the console's own principal. Stalwart expands a list at RCPT and keeps addresses unique
/// across accounts, aliases and lists itself; a clash comes back as <c>primaryKeyViolation</c>, mapped to Conflict.
/// </summary>
internal sealed partial class StalwartMailingLists(IStalwartManagementClient client, ILogger<StalwartMailingLists> logger) : IMailingLists
{
    private static readonly string[] ListProperties = ["name", "domainId", "emailAddress", "aliases", "recipients"];

    public async Task<Result<MailingList>> FindByAddressAsync(MailAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        Result<StalwartDomain> domain = await StalwartMailDomains.FindDomainAsync(client, address.Domain.Name, ["name"], cancellationToken).ConfigureAwait(false);
        if (!domain.IsSuccess)
        {
            return domain.Error.Kind == ErrorKind.NotFound ? NotFound(address) : domain.Error;
        }

        // Only "text" is a supported filter (1.1); its matching rule is not documented, so confirm the address.
        var request = new JmapRequestBuilder();
        var query = request.Add(StalwartMethods.MailingListQuery, new StalwartQueryRequest<MailingListFilter> { Filter = new MailingListFilter(address.LocalPart) });
        var get = request.Add(StalwartMethods.MailingListGet, new StalwartGetRequest { Properties = ListProperties }, query.Ref("ids", JmapPaths.Ids));
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Result<GetResponse<StalwartMailingList>> lists = response.Bind(r => r.Get(get));
        if (!lists.IsSuccess)
        {
            return lists.Error;
        }

        StalwartMailingList? match = lists.Value.List.FirstOrDefault(l => address.Matches(l.EmailAddress)
            || (l.EmailAddress is null && string.Equals(l.Name, address.LocalPart, StringComparison.OrdinalIgnoreCase) && l.DomainId == domain.Value.Id));
        return match is null ? NotFound(address) : await ToMailingListAsync(match, address, domain.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MailingList>> CreateAsync(MailAddress address, IReadOnlyList<MailAddress> aliases, IReadOnlyList<string> recipients, string description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(recipients);
        Result<Dictionary<string, string>> domainIds = await DomainIdsAsync(aliases.Prepend(address), cancellationToken).ConfigureAwait(false);
        if (!domainIds.IsSuccess)
        {
            return domainIds.Error;
        }

        var list = new JsonObject
        {
            ["name"] = address.LocalPart,
            ["domainId"] = domainIds.Value[address.Domain.Name],
            ["description"] = description,
            ["aliases"] = Aliases(aliases, domainIds.Value),
            ["recipients"] = Recipients(recipients),
        };
        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.MailingListSet, new StalwartSetRequest { Create = new Dictionary<string, JsonObject> { ["l"] = list } });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Result<SetResponse<StalwartCreated>> result = response.Bind(r => r.Get(set));
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        if (result.Value.NotCreated?.TryGetValue("l", out SetError? refused) == true)
        {
            return StalwartMethods.FromSetError(refused, "l");
        }

        Result<StalwartCreated> created = result.Value.Created("l");
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        LogCreated(logger, created.Value.Id, recipients.Count);
        return new MailingList(created.Value.Id, address.Value) { Aliases = [.. aliases.Select(static a => a.Value)], Recipients = recipients };
    }

    public async Task<Result> UpdateAsync(string listId, IReadOnlyList<MailAddress> aliases, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listId);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(recipients);
        Result<Dictionary<string, string>> domainIds = await DomainIdsAsync(aliases, cancellationToken).ConfigureAwait(false);
        if (!domainIds.IsSuccess)
        {
            return domainIds.Error;
        }

        // Both sets are replaced whole; the next message to the list goes to the new recipients (docs).
        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.MailingListSet, new StalwartSetRequest
        {
            Update = new Dictionary<string, JsonObject>
            {
                [listId] = new JsonObject { ["aliases"] = Aliases(aliases, domainIds.Value), ["recipients"] = Recipients(recipients) },
            },
        });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Result<SetResponse<StalwartCreated>> result = response.Bind(r => r.Get(set));
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        if (result.Value.NotUpdated?.TryGetValue(listId, out SetError? refused) == true)
        {
            return StalwartMethods.FromSetError(refused, listId);
        }

        Result updated = result.Value.Updated(listId);
        if (updated.IsSuccess)
        {
            LogUpdated(logger, listId, recipients.Count);
        }

        return updated;
    }

    public async Task<Result> DestroyAsync(string listId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listId);
        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.MailingListSet, new StalwartSetRequest { Destroy = [listId] });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Result destroyed = response.Bind(r => r.Get(set)).Match(r => r.Destroyed(listId), static e => Result.Failure(e));
        if (destroyed.IsSuccess)
        {
            LogDestroyed(logger, listId);
        }

        return destroyed;
    }

    public async Task<Result> SetSenderRuleAsync(MailAddress list, SenderRule? rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);
        var read = new JmapRequestBuilder();
        var query = read.Add(StalwartMethods.SieveSystemScriptQuery, new StalwartQueryRequest<object>());
        var get = read.Add(StalwartMethods.SieveSystemScriptGet, new StalwartGetRequest { Properties = ["name", "contents", "isActive"] }, query.Ref("ids", JmapPaths.Ids));
        Result<GetResponse<StalwartSieveScript>> scripts = (await client.SendAsync(read, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(get));
        if (!scripts.IsSuccess)
        {
            return scripts.Error;
        }

        StalwartSieveScript? script = scripts.Value.List.FirstOrDefault(static s => string.Equals(s.Name, ListSenderScript.Name, StringComparison.Ordinal));
        string contents = ListSenderScript.With(script?.Contents, list, rule);
        if (script is { IsActive: true } && string.Equals(script.Contents, contents, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        // Written and reloaded in one request: the rule applies from the next RCPT (step 0 c).
        var write = new JmapRequestBuilder();
        StalwartSetRequest change = script is null
            ? new StalwartSetRequest
            {
                Create = new Dictionary<string, JsonObject>
                {
                    ["s"] = new JsonObject { ["name"] = ListSenderScript.Name, ["description"] = "Who may send to each list (Nightware console)", ["isActive"] = true, ["contents"] = contents },
                },
            }
            : new StalwartSetRequest { Update = new Dictionary<string, JsonObject> { [script.Id] = new JsonObject { ["contents"] = contents, ["isActive"] = true } } };
        var set = write.Add(StalwartMethods.SieveSystemScriptSet, change);
        var reload = write.Add(StalwartMethods.ActionSet, StalwartMailDomains.ReloadSettings());
        Result<JmapResponse> response = await client.SendAsync(write, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        Result written = response.Value.Get(set).Match(r => script is null ? r.Created("s").ToResult() : r.Updated(script.Id), static e => Result.Failure(e));
        if (!written.IsSuccess)
        {
            return written;
        }

        LogRuleSet(logger, list.Value, rule is not null);
        return response.Value.Get(reload).Bind(r => r.Created("r")).ToResult();
    }

    /// <summary>
    /// The list whose address or alias is <paramref name="address"/>, or null. Reads every list: lists are few, and
    /// the only filter (<c>text</c>) is not documented to match aliases.
    /// </summary>
    internal static async Task<Result<StalwartMailingList?>> FindHolderAsync(IStalwartManagementClient client, string address, string local, string domainId, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var query = request.Add(StalwartMethods.MailingListQuery, new StalwartQueryRequest<MailingListFilter>());
        var get = request.Add(StalwartMethods.MailingListGet, new StalwartGetRequest { Properties = ListProperties }, query.Ref("ids", JmapPaths.Ids));
        Result<GetResponse<StalwartMailingList>> lists = (await client.SendAsync(request, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(get));
        return lists.IsSuccess
            ? lists.Value.List.FirstOrDefault(l => string.Equals(l.EmailAddress?.Trim(), address, StringComparison.OrdinalIgnoreCase)
                || StalwartMethods.ReadAliases(l.Aliases).Any(a => a.DomainId == domainId && string.Equals(a.Name, local, StringComparison.OrdinalIgnoreCase)))
            : lists.Error;
    }

    private static Error NotFound(MailAddress address) => Error.NotFound($"No list has the address {address}.", address.Value);

    private static JsonObject Recipients(IEnumerable<string> recipients)
    {
        var set = new JsonObject();
        foreach (string recipient in recipients.Select(static r => r.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal))
        {
            set[recipient] = true;
        }

        return set;
    }

    /// <summary>As on accounts: a map keyed by index (an array is refused with <c>invalidPatch</c>).</summary>
    private static JsonObject Aliases(IReadOnlyList<MailAddress> aliases, Dictionary<string, string> domainIds)
    {
        var list = new JsonObject();
        for (int i = 0; i < aliases.Count; i++)
        {
            list[i.ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["name"] = aliases[i].LocalPart, ["domainId"] = domainIds[aliases[i].Domain.Name] };
        }

        return list;
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

    private async Task<Result<MailingList>> ToMailingListAsync(StalwartMailingList list, MailAddress address, StalwartDomain lookedUp, CancellationToken cancellationToken)
    {
        IReadOnlyList<StalwartAlias> aliases = StalwartMethods.ReadAliases(list.Aliases);
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { [lookedUp.Id] = lookedUp.Name ?? address.Domain.Name };
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

        return new MailingList(list.Id, list.EmailAddress ?? address.Value)
        {
            Aliases = [.. aliases.Where(a => names.ContainsKey(a.DomainId)).Select(a => $"{a.Name}@{names[a.DomainId]}")],
            Recipients = StalwartMethods.ReadSet(list.Recipients),
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created Stalwart list {ListId} with {Count} recipient(s)")]
    private static partial void LogCreated(ILogger logger, string listId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Set {Count} recipient(s) on Stalwart list {ListId}")]
    private static partial void LogUpdated(ILogger logger, string listId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Destroyed Stalwart list {ListId}")]
    private static partial void LogDestroyed(ILogger logger, string listId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Set the sender rule of list {List} (restricted: {Restricted})")]
    private static partial void LogRuleSet(ILogger logger, string list, bool restricted);
}
