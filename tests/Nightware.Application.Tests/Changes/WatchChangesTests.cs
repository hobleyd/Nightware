using Microsoft.Extensions.Time.Testing;

using Nightware.Application.Changes;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Changes;

public sealed class WatchChangesTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers each read with the next scripted result, repeating the last.</summary>
    private sealed class ScriptedProbe(ChangeArea area, params Result<string>[] answers) : IChangeStateProbe
    {
        private int _next;

        public ChangeArea Area => area;

        public int Reads => _next;

        public Task<Result<string>> StateAsync(MailboxRef mailbox, CancellationToken cancellationToken) =>
            Task.FromResult(answers[Math.Min(_next++, answers.Length - 1)]);
    }

    private static Result<string> State(string s) => Result<string>.Success(s);

    private async Task<WatchEvent> NextRoundAsync(IAsyncEnumerator<WatchEvent> watch)
    {
        var moved = watch.MoveNextAsync();
        _time.Advance(Interval);
        (await moved).ShouldBeTrue();
        return watch.Current;
    }

    [Fact]
    public async Task The_baseline_names_the_areas_and_only_later_moves_are_signalled()
    {
        var mail = new ScriptedProbe(ChangeArea.Mail, State("e1|m1"), State("e1|m1"), State("e2|m1"));
        var calendar = new ScriptedProbe(ChangeArea.Calendar, State("c1"), State("c2"), State("c2"));
        await using var watch = new WatchChanges([mail, calendar], _time).ExecuteAsync(Me, Interval, Ct).GetAsyncEnumerator(Ct);

        (await watch.MoveNextAsync()).ShouldBeTrue();
        watch.Current.ShouldBeOfType<WatchEvent.Started>().Areas.ShouldBe([ChangeArea.Mail, ChangeArea.Calendar]);

        (await NextRoundAsync(watch)).ShouldBeOfType<WatchEvent.Round>().Result.Changed.ShouldBe([ChangeArea.Calendar]);
        (await NextRoundAsync(watch)).ShouldBeOfType<WatchEvent.Round>().Result.Changed.ShouldBe([ChangeArea.Mail]);
        (await NextRoundAsync(watch)).ShouldBeOfType<WatchEvent.Round>().Result.ShouldBe(ChangeRound.Quiet);
    }

    [Fact]
    public async Task An_area_the_mailbox_does_not_have_is_dropped_and_never_read_again()
    {
        var mail = new ScriptedProbe(ChangeArea.Mail, State("e1"));
        var calendar = new ScriptedProbe(ChangeArea.Calendar, Error.NotFound("no calendar"));
        await using var watch = new WatchChanges([mail, calendar], _time).ExecuteAsync(Me, Interval, Ct).GetAsyncEnumerator(Ct);

        (await watch.MoveNextAsync()).ShouldBeTrue();
        watch.Current.ShouldBeOfType<WatchEvent.Started>().Areas.ShouldBe([ChangeArea.Mail]);
        await NextRoundAsync(watch);

        calendar.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_last_state_so_a_change_made_meanwhile_is_still_signalled()
    {
        var mail = new ScriptedProbe(ChangeArea.Mail, State("e1"), Error.BackendUnavailable("blip"), State("e2"));
        await using var watch = new WatchChanges([mail], _time).ExecuteAsync(Me, Interval, Ct).GetAsyncEnumerator(Ct);
        await watch.MoveNextAsync();

        (await NextRoundAsync(watch)).ShouldBeOfType<WatchEvent.Round>().Result.ShouldBe(ChangeRound.Quiet);
        (await NextRoundAsync(watch)).ShouldBeOfType<WatchEvent.Round>().Result.Changed.ShouldBe([ChangeArea.Mail]);
    }

    [Fact]
    public async Task An_area_unreadable_at_the_baseline_is_watched_and_its_first_state_is_not_a_change()
    {
        var mail = new ScriptedProbe(ChangeArea.Mail, Error.BackendUnavailable("blip"), State("e1"), State("e2"));
        await using var watch = new WatchChanges([mail], _time).ExecuteAsync(Me, Interval, Ct).GetAsyncEnumerator(Ct);

        (await watch.MoveNextAsync()).ShouldBeTrue();
        watch.Current.ShouldBeOfType<WatchEvent.Started>().Areas.ShouldBe([ChangeArea.Mail]);
        (await NextRoundAsync(watch)).ShouldBeOfType<WatchEvent.Round>().Result.ShouldBe(ChangeRound.Quiet);
        (await NextRoundAsync(watch)).ShouldBeOfType<WatchEvent.Round>().Result.Changed.ShouldBe([ChangeArea.Mail]);
    }

    [Fact]
    public async Task A_credential_that_stops_working_ends_the_watch()
    {
        var mail = new ScriptedProbe(ChangeArea.Mail, State("e1"), Error.BackendAuthFailed("token expired"));
        await using var watch = new WatchChanges([mail], _time).ExecuteAsync(Me, Interval, Ct).GetAsyncEnumerator(Ct);
        await watch.MoveNextAsync();

        (await NextRoundAsync(watch)).ShouldBeOfType<WatchEvent.Ended>().Reason.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
        (await watch.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_credential_refused_at_the_baseline_ends_before_starting()
    {
        var mail = new ScriptedProbe(ChangeArea.Mail, Error.BackendAuthFailed("refused"));
        await using var watch = new WatchChanges([mail], _time).ExecuteAsync(Me, Interval, Ct).GetAsyncEnumerator(Ct);

        (await watch.MoveNextAsync()).ShouldBeTrue();
        watch.Current.ShouldBeOfType<WatchEvent.Ended>();
        (await watch.MoveNextAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelling_stops_the_watch_while_it_waits()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var mail = new ScriptedProbe(ChangeArea.Mail, State("e1"));
        await using var watch = new WatchChanges([mail], _time).ExecuteAsync(Me, Interval, cts.Token).GetAsyncEnumerator(cts.Token);
        await watch.MoveNextAsync();

        var waiting = watch.MoveNextAsync();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting);
    }

    [Fact]
    public void The_interval_must_be_positive() =>
        Should.Throw<ArgumentOutOfRangeException>(async () =>
            await new WatchChanges([], _time).ExecuteAsync(Me, TimeSpan.Zero, Ct).GetAsyncEnumerator(Ct).MoveNextAsync());
}
