using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Npgsql;

using NpgsqlTypes;

using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Subscriptions;

/// <summary>
/// <see cref="ISubscriptionItems"/> in the subscription store's Postgres: one row per (subscription, item), the item
/// kept as the first 8 bytes of SHA-256 of its id (never the id, which can carry a mailbox's structure), so a large
/// folder costs 16 bytes an item and only the ids of a poll's changes are looked up. Rows go with their
/// subscription (<c>ON DELETE CASCADE</c>).
/// </summary>
internal sealed class PostgresSubscriptionItems(NpgsqlDataSource database, SubscriptionSchema schema) : ISubscriptionItems
{
    public async Task<Result> ReplaceAsync(string subscriptionId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var clear = new NpgsqlCommand("DELETE FROM nightware_subscription_items WHERE subscription_id = $1", connection, transaction))
        {
            clear.Parameters.Add(new NpgsqlParameter { Value = subscriptionId });
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertAsync(connection, transaction, subscriptionId, ids, cancellationToken).ConfigureAwait(false);
        await using (var mark = new NpgsqlCommand("UPDATE nightware_subscriptions SET items_tracked = true WHERE id = $1", connection, transaction))
        {
            mark.Parameters.Add(new NpgsqlParameter { Value = subscriptionId });
            if (await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return Error.NotFound("The object was not found.", "id");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<IReadOnlySet<string>?>> KnownAsync(string subscriptionId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        var byHash = ids.Distinct(StringComparer.Ordinal).ToDictionary(Hash, static id => id);
        await using var command = database.CreateCommand("""
            SELECT s.items_tracked, i.item_hash
            FROM nightware_subscriptions s
            LEFT JOIN nightware_subscription_items i ON i.subscription_id = s.id AND i.item_hash = ANY($2)
            WHERE s.id = $1
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = subscriptionId });
        command.Parameters.Add(new NpgsqlParameter { Value = byHash.Keys.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tracked = false;
        var known = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tracked = reader.GetBoolean(0);
            if (!reader.IsDBNull(1) && byHash.TryGetValue(reader.GetInt64(1), out var id))
            {
                known.Add(id);
            }
        }

        return tracked ? known : Result<IReadOnlySet<string>?>.Success(null);
    }

    public async Task<Result> RecordAsync(string subscriptionId, IReadOnlyCollection<string> added, IReadOnlyCollection<string> removed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(removed);
        if (added.Count == 0 && removed.Count == 0)
        {
            return Result.Success();
        }

        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (removed.Count > 0)
        {
            await using var delete = new NpgsqlCommand("DELETE FROM nightware_subscription_items WHERE subscription_id = $1 AND item_hash = ANY($2)", connection, transaction);
            delete.Parameters.Add(new NpgsqlParameter { Value = subscriptionId });
            delete.Parameters.Add(new NpgsqlParameter { Value = removed.Select(Hash).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertAsync(connection, transaction, subscriptionId, added, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private static async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string subscriptionId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        // Only for items whose subscription still exists (a concurrent delete wins).
        await using var insert = new NpgsqlCommand("""
            INSERT INTO nightware_subscription_items (subscription_id, item_hash)
            SELECT $1, h FROM unnest($2::bigint[]) AS h WHERE EXISTS (SELECT 1 FROM nightware_subscriptions WHERE id = $1)
            ON CONFLICT DO NOTHING
            """, connection, transaction);
        insert.Parameters.Add(new NpgsqlParameter { Value = subscriptionId });
        insert.Parameters.Add(new NpgsqlParameter { Value = ids.Select(Hash).Distinct().ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static long Hash(string id) => BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
}
