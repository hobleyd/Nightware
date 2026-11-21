using Npgsql;

using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Adapters.Subscriptions;

/// <summary>
/// <see cref="ISubscriptionStore"/> in its own Postgres (ADR 0006 D10). The table is created on first use. The refresh
/// credential is sealed by <see cref="CredentialSealer"/> and never leaves the store through a read; rows are always
/// filtered by creator, so one user cannot see or touch another's subscription.
/// </summary>
internal sealed class PostgresSubscriptionStore(NpgsqlDataSource database, CredentialSealer sealer, SubscriptionSchema schema) : ISubscriptionStore
{
    internal const string Columns = "id, creator_id, resource, mailbox, kind, folder, change_type, notification_url, lifecycle_url, client_state, application_id, expiration, sync_state";

    public async Task<Result<Subscription>> CreateAsync(Subscription subscription, string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand($"""
            INSERT INTO nightware_subscriptions ({Columns}, credential)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            """);
        Bind(command, subscription);
        command.Parameters.Add(new NpgsqlParameter { Value = sealer.Seal(subscription.Id, refreshToken) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return subscription;
    }

    public async Task<Result<Subscription>> GetAsync(string creatorId, string id, CancellationToken cancellationToken)
    {
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand($"SELECT {Columns} FROM nightware_subscriptions WHERE creator_id = $1 AND id = $2");
        command.Parameters.Add(new NpgsqlParameter { Value = creatorId });
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : NotFound();
    }

    public async Task<Result<IReadOnlyList<Subscription>>> ListAsync(string creatorId, CancellationToken cancellationToken)
    {
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand($"SELECT {Columns} FROM nightware_subscriptions WHERE creator_id = $1 ORDER BY expiration, id");
        command.Parameters.Add(new NpgsqlParameter { Value = creatorId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var subscriptions = new List<Subscription>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            subscriptions.Add(Read(reader));
        }

        return subscriptions;
    }

    public async Task<Result<Subscription>> UpdateAsync(Subscription subscription, string? refreshToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand($"""
            UPDATE nightware_subscriptions
               SET resource = $3, mailbox = $4, kind = $5, folder = $6, change_type = $7, notification_url = $8, lifecycle_url = $9,
                   client_state = $10, application_id = $11, expiration = $12, sync_state = $13, credential = COALESCE($14, credential)
             WHERE id = $1 AND creator_id = $2
            """);
        Bind(command, subscription);
        command.Parameters.Add(new NpgsqlParameter { Value = refreshToken is null ? DBNull.Value : sealer.Seal(subscription.Id, refreshToken), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1 ? subscription : NotFound();
    }

    public async Task<Result> DeleteAsync(string creatorId, string id, CancellationToken cancellationToken)
    {
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand("DELETE FROM nightware_subscriptions WHERE creator_id = $1 AND id = $2");
        command.Parameters.Add(new NpgsqlParameter { Value = creatorId });
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1 ? Result.Success() : NotFound();
    }

    private static void Bind(NpgsqlCommand command, Subscription s)
    {
        static object Nullable(object? value) => value ?? DBNull.Value;
        foreach (var value in new object?[]
        {
            s.Id, s.CreatorId, s.Resource, s.Target.Mailbox.Address, (short)s.Target.Kind, s.Target.Folder, (short)s.ChangeType,
            s.NotificationUrl.AbsoluteUri, s.LifecycleNotificationUrl?.AbsoluteUri, s.ClientState, s.ApplicationId, s.ExpirationDateTime.UtcDateTime, s.SyncState,
        })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = Nullable(value) });
        }

        // Typed so a null still has a type Postgres can infer.
        foreach (var index in new[] { 5, 8, 9, 10, 12 })
        {
            command.Parameters[index].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
        }
    }

    internal static Subscription Read(NpgsqlDataReader r)
    {
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new Subscription(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            new SubscriptionTarget(new MailboxRef(r.GetString(3)), (SubscriptionResourceKind)r.GetInt16(4), Text(5)),
            (SubscriptionChangeType)r.GetInt16(6),
            new Uri(r.GetString(7)),
            new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(11), DateTimeKind.Utc)))
        {
            LifecycleNotificationUrl = Text(8) is { } lifecycle ? new Uri(lifecycle) : null,
            ClientState = Text(9),
            ApplicationId = Text(10),
            SyncState = Text(12),
        };
    }

    private static Error NotFound() => Error.NotFound("The object was not found.", "id");
}

/// <summary>Creates the subscription table once per process (idempotent DDL).</summary>
internal sealed class SubscriptionSchema(NpgsqlDataSource database) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;

    public void Dispose() => _gate.Dispose();

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_ready)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_ready)
            {
                await using var command = database.CreateCommand("""
                    CREATE TABLE IF NOT EXISTS nightware_subscriptions (
                        id               text PRIMARY KEY,
                        creator_id       text NOT NULL,
                        resource         text NOT NULL,
                        mailbox          text NOT NULL,
                        kind             smallint NOT NULL,
                        folder           text,
                        change_type      smallint NOT NULL,
                        notification_url text NOT NULL,
                        lifecycle_url    text,
                        client_state     text,
                        application_id   text,
                        expiration       timestamptz NOT NULL,
                        sync_state       text,
                        credential       bytea NOT NULL,
                        next_poll        timestamptz NOT NULL DEFAULT now(),
                        leased_until     timestamptz,
                        failures         integer NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS nightware_subscriptions_creator ON nightware_subscriptions (creator_id);
                    CREATE INDEX IF NOT EXISTS nightware_subscriptions_due ON nightware_subscriptions (next_poll);
                    """);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _ready = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
