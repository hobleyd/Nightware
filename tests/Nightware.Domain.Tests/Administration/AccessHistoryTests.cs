using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class AccessHistoryTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 18, 9, 12, 0, TimeSpan.Zero);

    private static AccessEvent Offboarded(string by = "ops") =>
        new(At, AccessEventKind.Offboarded, by, [new SystemState(BackendKind.Mail, LinkState.Error), new SystemState(BackendKind.Groupware, LinkState.Missing)]);

    [Fact]
    public void An_entry_is_one_short_line_that_reads_back_the_same()
    {
        string line = Offboarded().Format();

        line.ShouldBe("2026-11-18T09:12:00Z|Offboarded|ops|Mail:Error,Groupware:Missing");
        AccessEvent back = AccessEvent.Parse(line)!;
        back.At.ShouldBe(At);
        back.Kind.ShouldBe(AccessEventKind.Offboarded);
        back.By.ShouldBe("ops");
        back.Systems.ShouldBe([new SystemState(BackendKind.Mail, LinkState.Error), new SystemState(BackendKind.Groupware, LinkState.Missing)]);
    }

    [Fact]
    public void A_name_cannot_break_the_line_or_overflow_it()
    {
        string line = Offboarded(by: "a|b,c:d" + new string('x', 300)).Format();

        line.Length.ShouldBeLessThanOrEqualTo(255);
        AccessEvent.Parse(line)!.By.ShouldStartWith("abcd");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("2026-11-18T09:12:00Z|Exploded|ops|")]
    [InlineData("yesterday|Offboarded|ops|")]
    public void A_line_the_console_cannot_read_is_skipped_not_guessed(string? line) =>
        AccessEvent.Parse(line).ShouldBeNull();

    [Fact]
    public void A_new_entry_goes_first_and_only_the_last_ten_are_kept()
    {
        string[] existing = [.. Enumerable.Range(0, 12).Select(i => Offboarded().Format()), "garbage"];

        IReadOnlyList<string> history = AccessEvent.Prepend(new AccessEvent(At.AddHours(1), AccessEventKind.Reinstated, "ops", []), existing);

        history.Count.ShouldBe(AccessEvent.Keep);
        history[0].ShouldContain("|Reinstated|");
        history.ShouldNotContain("garbage");
    }
}
