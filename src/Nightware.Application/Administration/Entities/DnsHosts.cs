using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>An entity's DNS host, opened with its credential: <see cref="IDnsHosting"/> without the token to pass.</summary>
public sealed class DnsHostSession(DnsConnection connection, IDnsHosting hosting)
{
    public DnsProvider Provider => connection.Provider;

    public Task<Result<DnsZone>> FindZoneAsync(MailDomain domain, CancellationToken cancellationToken) =>
        hosting.FindZoneAsync(connection.Token, domain, cancellationToken);

    public Task<Result<IReadOnlyList<ZoneRecord>>> ListRecordsAsync(DnsZone zone, CancellationToken cancellationToken) =>
        hosting.ListRecordsAsync(connection.Token, zone, cancellationToken);

    public Task<Result> ApplyAsync(DnsZone zone, DnsChange change, CancellationToken cancellationToken) =>
        hosting.ApplyAsync(connection.Token, zone, change, cancellationToken);
}

/// <summary>The DNS hosts the console can publish to (one <see cref="IDnsHosting"/> each), and each entity's own.</summary>
public sealed class DnsHosts(DnsTokenVault vault, IEnumerable<IDnsHosting> hosts)
{
    private readonly IReadOnlyList<IDnsHosting> _hosts = [.. hosts];

    /// <summary>The hosts an entity can connect, in <see cref="DnsProvider"/> order.</summary>
    public IReadOnlyList<DnsProvider> Available => [.. _hosts.Select(static h => h.Provider).Distinct().Order()];

    /// <summary>The last registered for <paramref name="provider"/>, as dependency injection resolves a single service.</summary>
    public Result<IDnsHosting> For(DnsProvider provider) =>
        _hosts.LastOrDefault(h => h.Provider == provider) is { } hosting
            ? Result<IDnsHosting>.Success(hosting)
            : Error.Validation($"This console cannot publish to {provider.Name()}.", "dnsProvider");

    /// <summary>The entity's host with its credential; Validation (target <c>dnsToken</c>) when it has none or it cannot be read.</summary>
    public async Task<Result<DnsHostSession>> ConnectAsync(string entityId, CancellationToken cancellationToken)
    {
        Result<DnsConnection> connection = await vault.OpenAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!connection.IsSuccess)
        {
            return connection.Error;
        }

        Result<IDnsHosting> hosting = For(connection.Value.Provider);
        return hosting.IsSuccess ? new DnsHostSession(connection.Value, hosting.Value) : hosting.Error;
    }
}
