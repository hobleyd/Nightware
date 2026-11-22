using System.Runtime.CompilerServices;

using Nightware.Domain.Kernel;

namespace Nightware.Application.Changes;

/// <summary>One round of a watch: the areas whose state moved since the round before (often none).</summary>
public sealed record ChangeRound(IReadOnlyList<ChangeArea> Changed)
{
    public static readonly ChangeRound Quiet = new([]);
}

/// <summary>
/// The change stream's watcher (ADR 0007): reads every area's state each <c>interval</c> as the caller and reports
/// the areas whose state moved. Nothing is stored; the stream lives as long as the caller's request.
/// </summary>
/// <remarks>
/// <para>The first round only records where each area stands: the client syncs once when the stream opens, so
/// nothing that came before needs announcing. An area the mailbox does not have (NotFound, Unsupported) is dropped
/// for the life of the watch. Any other failure leaves that area's last state in place and it is read again next
/// round, so a backend blip neither ends the stream nor fires a signal; a state read after a failure is compared
/// with the one before it, and a change made meanwhile is still reported.</para>
/// <para>The watch ends when the caller's credential stops working (BackendAuthFailed, Unauthorized): the client
/// reconnects with a fresh token.</para>
/// </remarks>
public sealed class WatchChanges(IEnumerable<IChangeStateProbe> probes, TimeProvider time)
{
    private readonly IReadOnlyList<IChangeStateProbe> _probes = [.. probes];

    /// <summary>
    /// The areas <paramref name="mailbox"/> has, read once as the baseline: the first item. Then one
    /// <see cref="ChangeRound"/> per <paramref name="interval"/> until the caller cancels or its credential fails.
    /// </summary>
    public async IAsyncEnumerable<WatchEvent> ExecuteAsync(
        MailboxRef mailbox, TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");
        }

        var watched = new Dictionary<IChangeStateProbe, string?>();
        foreach (var probe in _probes)
        {
            var state = await probe.StateAsync(mailbox, cancellationToken).ConfigureAwait(false);
            if (state.IsSuccess)
            {
                watched[probe] = state.Value;
            }
            else if (EndsWatch(state.Error))
            {
                yield return new WatchEvent.Ended(state.Error);
                yield break;
            }
            else if (!IsAbsent(state.Error))
            {
                watched[probe] = null;
            }
        }

        yield return new WatchEvent.Started([.. watched.Keys.Select(static p => p.Area).Distinct().Order()]);

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, time, cancellationToken).ConfigureAwait(false);
            var changed = new SortedSet<ChangeArea>();
            foreach (var probe in watched.Keys.ToList())
            {
                var state = await probe.StateAsync(mailbox, cancellationToken).ConfigureAwait(false);
                if (!state.IsSuccess)
                {
                    if (EndsWatch(state.Error))
                    {
                        yield return new WatchEvent.Ended(state.Error);
                        yield break;
                    }

                    continue;
                }

                if (watched[probe] is { } before && !string.Equals(before, state.Value, StringComparison.Ordinal))
                {
                    changed.Add(probe.Area);
                }

                watched[probe] = state.Value;
            }

            yield return new WatchEvent.Round(changed.Count == 0 ? ChangeRound.Quiet : new ChangeRound([.. changed]));
        }
    }

    private static bool EndsWatch(Error error) => error.Kind is ErrorKind.BackendAuthFailed or ErrorKind.Unauthorized;

    private static bool IsAbsent(Error error) => error.Kind is ErrorKind.NotFound or ErrorKind.Unsupported or ErrorKind.Forbidden;
}

/// <summary>What <see cref="WatchChanges"/> reports.</summary>
public abstract record WatchEvent
{
    private WatchEvent()
    {
    }

    /// <summary>The baseline is read; these are the areas being watched.</summary>
    public sealed record Started(IReadOnlyList<ChangeArea> Areas) : WatchEvent;

    /// <summary>One round of reads.</summary>
    public sealed record Round(ChangeRound Result) : WatchEvent;

    /// <summary>The caller's credential stopped working; nothing follows.</summary>
    public sealed record Ended(Error Reason) : WatchEvent;
}
