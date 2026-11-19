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

/// <summary>Stalwart accounts (<c>x:Account</c>), managed with the console's own principal (ADR 0002 D3, D5, D10).</summary>
internal sealed partial class StalwartMailAccounts(
    IStalwartManagementClient client,
    IOptions<StalwartManagementOptions> options,
    ILogger<StalwartMailAccounts> logger) : IMailAccounts
{
    /// <summary>
    /// <c>@type</c> is requested explicitly: only <c>User</c> is an individual mailbox, and an account
    /// whose type is not reported is treated as not individual, so it is never linked or destroyed.
    /// </summary>
    private static readonly string[] AccountProperties = ["@type", "name", "domainId", "emailAddress", "aliases", "roles", "permissions"];

    public async Task<Result<MailAccount>> FindByAddressAsync(string address, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        string trimmed = address.Trim();
        int at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1)
        {
            return Error.Validation($"'{trimmed}' is not an e-mail address.", "address");
        }

        string local = trimmed[..at];
        Result<StalwartDomain> domain = await StalwartMailDomains.FindDomainAsync(client, trimmed[(at + 1)..].TrimEnd('.'), ["name"], cancellationToken).ConfigureAwait(false);
        if (!domain.IsSuccess)
        {
            // Primary addresses and aliases both name a Stalwart domain id: no domain, no holder.
            return domain.Error.Kind == ErrorKind.NotFound ? NotFound(trimmed) : domain.Error;
        }

        // 1. Primary address: the documented name + domainId filter, confirmed against emailAddress
        //    because the filter's matching rule (exact, prefix, substring) is not documented.
        Result<AccountPage> named = await QueryAsync(new AccountFilter { Name = local.ToLowerInvariant(), DomainId = domain.Value.Id }, 0, options.Value.AccountPageSize, cancellationToken).ConfigureAwait(false);
        if (!named.IsSuccess)
        {
            return named.Error;
        }

        StalwartAccount? match = named.Value.Accounts.FirstOrDefault(a => IsPrimary(a, trimmed));
        if (match is null)
        {
            // 2. Alias: Stalwart documents no filter by alias, so page through every account, bounded.
            Result<StalwartAccount?> scanned = await ScanForAsync(trimmed, local, domain.Value.Id, cancellationToken).ConfigureAwait(false);
            if (!scanned.IsSuccess)
            {
                return scanned.Error;
            }

            match = scanned.Value;
        }

        return match is null ? NotFound(trimmed) : await ToMailAccountAsync(match, domain.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MailAccount>> CreateAsync(MailAddress address, string description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        Result<StalwartDomain> domain = await StalwartMailDomains.FindDomainAsync(client, address.Domain.Name, ["name"], cancellationToken).ConfigureAwait(false);
        if (!domain.IsSuccess)
        {
            return DomainError(domain.Error, address.Domain.Name);
        }

        // As configure.sh creates alice and bob; aliases are set afterwards with SetAliasesAsync.
        var account = new JsonObject
        {
            ["@type"] = "User",
            ["name"] = address.LocalPart,
            ["domainId"] = domain.Value.Id,
            ["description"] = description,
        };
        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.AccountSet, new StalwartSetRequest { Create = new Dictionary<string, JsonObject> { ["a"] = account } });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // An address a list (or another account) holds comes back as primaryKeyViolation: a Conflict naming the holder.
        Result<StalwartCreated> created = response.Bind(r => r.Get(set)).Bind(static r =>
            r.NotCreated?.TryGetValue("a", out SetError? refused) == true ? StalwartMethods.FromSetError(refused, "a") : r.Created("a"));
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        LogCreated(logger, created.Value.Id);
        return new MailAccount(created.Value.Id, address.Value, IsIndividual: true);
    }

    /// <summary>
    /// Blocks or restores the account's sign-in (every credential: OIDC, app passwords, API keys) by merging away
    /// the <c>authenticate</c> permission; mail delivery is a separate permission and continues (verified on 1.1).
    /// </summary>
    public async Task<Result> SetEnabledAsync(string accountId, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        JsonObject permissions = enabled
            ? new JsonObject { ["@type"] = "Inherit" }
            : new JsonObject { ["@type"] = "Merge", ["enabledPermissions"] = new JsonObject(), ["disabledPermissions"] = new JsonObject { ["authenticate"] = true } };
        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.AccountSet, new StalwartSetRequest
        {
            Update = new Dictionary<string, JsonObject> { [accountId] = new JsonObject { ["permissions"] = permissions } },
        });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Match(r => r.Updated(accountId), static e => Result.Failure(e));
    }

    public async Task<Result> SetAdministratorAsync(string accountId, bool administrator, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.AccountSet, new StalwartSetRequest
        {
            Update = new Dictionary<string, JsonObject> { [accountId] = new JsonObject { ["roles"] = new JsonObject { ["@type"] = administrator ? "Admin" : "User" } } },
        });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Match(r => r.Updated(accountId), static e => Result.Failure(e));
    }

    public async Task<Result> SetAliasesAsync(string accountId, IReadOnlyList<MailAddress> aliases, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(aliases);

        var domainIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in aliases.Select(static a => a.Domain.Name).Distinct(StringComparer.Ordinal))
        {
            Result<StalwartDomain> domain = await StalwartMailDomains.FindDomainAsync(client, name, ["name"], cancellationToken).ConfigureAwait(false);
            if (!domain.IsSuccess)
            {
                return DomainError(domain.Error, name);
            }

            domainIds[name] = domain.Value.Id;
        }

        // The whole list is replaced. Stalwart writes lists as maps keyed by index ("0", "1", …): an
        // array is rejected with invalidPatch (Stalwart support forum, topic 770 — not in the docs).
        // "enable" is left to its default (true).
        var list = new JsonObject();
        for (int i = 0; i < aliases.Count; i++)
        {
            list[i.ToString(CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["name"] = aliases[i].LocalPart,
                ["domainId"] = domainIds[aliases[i].Domain.Name],
            };
        }

        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.AccountSet, new StalwartSetRequest
        {
            Update = new Dictionary<string, JsonObject> { [accountId] = new JsonObject { ["aliases"] = list } },
        });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Result updated = response.Bind(r => r.Get(set)).Match(
            r => r.NotUpdated?.TryGetValue(accountId, out SetError? refused) == true ? StalwartMethods.FromSetError(refused, accountId) : r.Updated(accountId),
            static e => Result.Failure(e));
        if (updated.IsSuccess)
        {
            LogAliasesSet(logger, accountId, aliases.Count);
        }

        return updated;
    }

    public async Task<Result<long>> GetUsageAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var request = new JmapRequestBuilder();
        var get = request.Add(StalwartMethods.AccountGet, new StalwartGetRequest { Ids = [accountId], Properties = ["usedDiskQuota"] });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Result<GetResponse<StalwartAccount>> accounts = response.Bind(r => r.Get(get));
        if (!accounts.IsSuccess)
        {
            return accounts.Error;
        }

        StalwartAccount? account = accounts.Value.List.FirstOrDefault(a => a.Id == accountId);
        if (account is null)
        {
            return Error.NotFound($"The mail server has no account {accountId}.", accountId);
        }

        return account.UsedDiskQuota is { } used
            ? used
            : JmapErrors.Unexpected($"The mail server did not report the usage of account {accountId}.");
    }

    public async Task<Result> DestroyAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.AccountSet, new StalwartSetRequest { Destroy = [accountId] });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Result destroyed = response.Bind(r => r.Get(set)).Match(r => r.Destroyed(accountId), static e => Result.Failure(e));
        if (destroyed.IsSuccess)
        {
            LogDestroyed(logger, accountId);
        }

        return destroyed;
    }

    private static bool IsPrimary(StalwartAccount account, string address) =>
        string.Equals(account.EmailAddress?.Trim(), address, StringComparison.OrdinalIgnoreCase);

    private static Error NotFound(string address) => Error.NotFound($"No mail account has the address {address}.", address);

    private static Error DomainError(Error error, string domain) =>
        error.Kind == ErrorKind.NotFound
            ? Error.Validation($"The mail server does not serve {domain}; add the domain to the entity first.", "domain")
            : error;

    private async Task<Result<AccountPage>> QueryAsync(AccountFilter? filter, int position, int limit, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var query = request.Add(StalwartMethods.AccountQuery, new StalwartQueryRequest<AccountFilter> { Filter = filter, Position = position, Limit = limit });
        var get = request.Add(StalwartMethods.AccountGet, new StalwartGetRequest { Properties = AccountProperties }, query.Ref("ids", JmapPaths.Ids));
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(query).Bind(q => r.Get(get).Map(g => new AccountPage(g.List, q.Ids.Count, q.Limit))));
    }

    private async Task<Result<StalwartAccount?>> ScanForAsync(string address, string local, string domainId, CancellationToken cancellationToken)
    {
        StalwartManagementOptions settings = options.Value;
        int position = 0;
        while (position < settings.MaxAccountsScanned)
        {
            int limit = Math.Min(settings.AccountPageSize, settings.MaxAccountsScanned - position);
            Result<AccountPage> page = await QueryAsync(null, position, limit, cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccess)
            {
                return page.Error;
            }

            StalwartAccount? match = page.Value.Accounts.FirstOrDefault(a => IsPrimary(a, address)
                || StalwartMethods.ReadAliases(a.Aliases).Any(x => x.DomainId == domainId && string.Equals(x.Name, local, StringComparison.OrdinalIgnoreCase)));

            // A server may cap the page below what was asked and says so in the response's limit
            // (RFC 8620 5.5): only a page shorter than the effective limit (or empty) is the last one.
            if (match is not null || page.Value.IdCount == 0 || page.Value.IdCount < (page.Value.ServerLimit ?? limit))
            {
                return match;
            }

            position += page.Value.IdCount;
        }

        LogScanExhausted(logger, settings.MaxAccountsScanned);
        return JmapErrors.Unexpected(
            $"Could not tell whether a mail account holds {address}: more than {settings.MaxAccountsScanned} accounts would have to be searched (StalwartManagement:MaxAccountsScanned).");
    }

    private async Task<Result<MailAccount>> ToMailAccountAsync(StalwartAccount account, StalwartDomain lookedUp, CancellationToken cancellationToken)
    {
        IReadOnlyList<StalwartAlias> aliases = StalwartMethods.ReadAliases(account.Aliases);
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { [lookedUp.Id] = lookedUp.Name ?? string.Empty };
        string[] unknown = [.. aliases.Select(static a => a.DomainId).Append(account.DomainId ?? lookedUp.Id).Distinct(StringComparer.Ordinal).Where(id => !names.ContainsKey(id))];
        if (unknown.Length > 0)
        {
            var request = new JmapRequestBuilder();
            var get = request.Add(StalwartMethods.DomainGet, new StalwartGetRequest { Ids = unknown, Properties = ["name"] });
            Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Result<GetResponse<StalwartDomain>> domains = response.Bind(r => r.Get(get));
            if (!domains.IsSuccess)
            {
                return domains.Error;
            }

            foreach (StalwartDomain domain in domains.Value.List.Where(static d => d.Name is not null))
            {
                names[domain.Id] = domain.Name!;
            }
        }

        string primary = account.EmailAddress
            ?? (account.Name is not null && account.DomainId is not null && names.TryGetValue(account.DomainId, out string? own) ? $"{account.Name}@{own}" : string.Empty);
        return new MailAccount(account.Id, primary, IsIndividual: string.Equals(account.Type, "User", StringComparison.Ordinal))
        {
            IsPrivileged = account.IsPrivileged,
            IsAdministrator = account.IsAdministrator,
            Enabled = account.IsEnabled,
            Aliases = [.. aliases.Where(a => names.ContainsKey(a.DomainId)).Select(a => $"{a.Name}@{names[a.DomainId]}")],
        };
    }

    /// <param name="IdCount">Ids the query returned on this page.</param>
    /// <param name="ServerLimit">The limit the server applied, when it reports one.</param>
    private sealed record AccountPage(IReadOnlyList<StalwartAccount> Accounts, int IdCount, int? ServerLimit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created Stalwart account {AccountId}")]
    private static partial void LogCreated(ILogger logger, string accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Set {Count} alias(es) on Stalwart account {AccountId}")]
    private static partial void LogAliasesSet(ILogger logger, string accountId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Destroyed Stalwart account {AccountId}")]
    private static partial void LogDestroyed(ILogger logger, string accountId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alias lookup gave up after {Limit} Stalwart accounts (StalwartManagement:MaxAccountsScanned)")]
    private static partial void LogScanExhausted(ILogger logger, int limit);
}