using Npgsql;

using Nightware.Application.Administration.Subscriptions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Adapters.Subscriptions.Administration;

/// <summary>
/// <see cref="IChangeSubscriptions"/> over the façade's subscription store, for the admin console: it reads the rows
/// (never the sealed credential) and deletes one with its credential and its seen items (cascade).
/// </summary>
internal sealed class PostgresChangeSubscriptions(NpgsqlDataSource database, SubscriptionSchema schema) : IChangeSubscriptions
{
    public async Task<Result<IReadOnlyList<ManagedSubscription>>> ListAsync(string? creatorId, CancellationToken cancellationToken)
    {
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand($"""
            SELECT {PostgresSubscriptionStore.Columns}, last_poll, failures, reauth_requested
              FROM nightware_subscriptions
             WHERE $1::text IS NULL OR creator_id = $1
             ORDER BY expiration
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)creatorId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<ManagedSubscription>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add(new ManagedSubscription(
                PostgresSubscriptionStore.Read(reader),
                Utc(reader, 13),
                reader.GetInt32(14),
                Utc(reader, 15)));
        }

        return found;
    }

    public async Task<Result<Subscription>> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand($"DELETE FROM nightware_subscriptions WHERE id = $1 RETURNING {PostgresSubscriptionStore.Columns}");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? PostgresSubscriptionStore.Read(reader)
            : Error.NotFound("The subscription was not found.", "subscriptionId");
    }

    private static DateTimeOffset? Utc(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}

/// <summary>The console's answer when it has no subscription store configured.</summary>
internal sealed class UnconfiguredChangeSubscriptions : IChangeSubscriptions
{
    public static readonly UnconfiguredChangeSubscriptions Instance = new();

    private static Error NotConfigured => Error.Unsupported("Change notifications are not configured on this console.");

    public Task<Result<IReadOnlyList<ManagedSubscription>>> ListAsync(string? creatorId, CancellationToken cancellationToken) =>
        Task.FromResult<Result<IReadOnlyList<ManagedSubscription>>>(NotConfigured);

    public Task<Result<Subscription>> RemoveAsync(string id, CancellationToken cancellationToken) => Task.FromResult<Result<Subscription>>(NotConfigured);
}
