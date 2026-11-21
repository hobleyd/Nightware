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

    /// <summary>The baseline state was read just now, so "now" is the first poll's starting point; it is due at once.</summary>
    public async Task<Result<Subscription>> CreateAsync(Subscription subscription, string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand($"""
            INSERT INTO nightware_subscriptions ({Columns}, credential, last_poll, next_poll)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, now(), '-infinity')
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
        // The sync state is the poller's ($13 is bound, typed, and not written): a renewal must not rewind it. A new credential
        // (a renewal is also a reauthorization) clears the backoff so polling resumes at once, whatever the clocks say.
        await using var command = database.CreateCommand($"""
            UPDATE nightware_subscriptions
               SET resource = $3, mailbox = $4, kind = $5, folder = $6, change_type = $7, notification_url = $8, lifecycle_url = $9,
                   client_state = $10, application_id = $11, expiration = $12, credential = COALESCE($14, credential),
                   failures = CASE WHEN $14 IS NULL THEN failures ELSE 0 END,
                   next_poll = CASE WHEN $14 IS NULL THEN next_poll ELSE '-infinity'::timestamptz END
             WHERE id = $1 AND creator_id = $2
            """);
        Bind(command, subscription);
        command.Parameters.Add(new NpgsqlParameter { Value = refreshToken is null ? DBNull.Value : sealer.Seal(subscription.Id, refreshToken), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return NotFound();
        }

        return await GetAsync(subscription.CreatorId, subscription.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(string creatorId, string id, CancellationToken cancellationToken)
    {
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand("DELETE FROM nightware_subscriptions WHERE creator_id = $1 AND id = $2");
        command.Parameters.Add(new NpgsqlParameter { Value = creatorId });
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1 ? Result.Success() : NotFound();
    }

    public async Task<Result<IReadOnlyList<LeasedSubscription>>> LeaseDueAsync(DateTimeOffset now, TimeSpan lease, int max, CancellationToken cancellationToken)
    {
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        var prefixed = string.Join(", ", Columns.Split(", ").Select(c => "s." + c));
        await using var command = database.CreateCommand($"""
            WITH due AS (
                SELECT id FROM nightware_subscriptions
                 WHERE next_poll <= $1 AND expiration > $1 AND (leased_until IS NULL OR leased_until < $1)
                 ORDER BY next_poll
                 LIMIT $3
                 FOR UPDATE SKIP LOCKED)
            UPDATE nightware_subscriptions s SET leased_until = $2 FROM due WHERE s.id = due.id
            RETURNING {prefixed}, s.credential, s.last_poll, s.failures
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = now.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter { Value = (now + lease).UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter { Value = max });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var leased = new List<LeasedSubscription>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var subscription = Read(reader);
            var credential = sealer.Open(subscription.Id, (byte[])reader[13]);
            var lastPoll = reader.IsDBNull(14) ? now : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(14), DateTimeKind.Utc));
            leased.Add(new LeasedSubscription(subscription, credential.IsSuccess ? credential.Value : null, lastPoll, reader.GetInt32(15)));
        }

        return leased;
    }

    public async Task<Result> CompletePollAsync(
        string id, string? syncState, string? refreshToken, DateTimeOffset? polledAt, DateTimeOffset nextPoll, int failures, CancellationToken cancellationToken)
    {
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand("""
            UPDATE nightware_subscriptions
               SET sync_state = COALESCE($2, sync_state), credential = COALESCE($3, credential), last_poll = COALESCE($4, last_poll),
                   next_poll = $5, failures = $6, leased_until = NULL
             WHERE id = $1
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)syncState ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = refreshToken is null ? DBNull.Value : sealer.Seal(id, refreshToken), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter { Value = polledAt is { } at ? at.UtcDateTime : DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = nextPoll.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter { Value = failures });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1 ? Result.Success() : NotFound();
    }

    public async Task<Result<IReadOnlyList<Subscription>>> RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await schema.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.CreateCommand($"DELETE FROM nightware_subscriptions WHERE expiration <= $1 RETURNING {Columns}");
        command.Parameters.Add(new NpgsqlParameter { Value = now.UtcDateTime });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var removed = new List<Subscription>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            removed.Add(Read(reader));
        }

        return removed;
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
                        last_poll        timestamptz,
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
