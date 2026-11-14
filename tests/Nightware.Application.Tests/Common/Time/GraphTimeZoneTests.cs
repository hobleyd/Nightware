using Nightware.Application.Common.Time;

namespace Nightware.Application.Tests.Common.Time;

public sealed class GraphTimeZoneTests
{
    [Theory]
    [InlineData("UTC")]
    [InlineData("utc")]
    [InlineData("Etc/UTC")]
    [InlineData("Coordinated Universal Time")]
    [InlineData("tzone://Microsoft/Utc")]
    public void Utc_spellings_resolve_to_utc(string name)
    {
        GraphTimeZone.TryFind(name, out var zone).ShouldBeTrue();
        zone.ShouldBe(TimeZoneInfo.Utc);
        GraphTimeZone.ToWindowsId(name).ShouldBe("UTC");
        GraphTimeZone.ToIanaId(name).ShouldBe("Etc/UTC");
    }

    [Theory]
    [InlineData("Australia/Sydney")]
    [InlineData("AUS Eastern Standard Time")]
    [InlineData("Europe/London")]
    [InlineData("GMT Standard Time")]
    public void Windows_and_iana_ids_resolve(string name)
    {
        GraphTimeZone.TryFind(name, out var zone).ShouldBeTrue();
        zone.ShouldNotBe(TimeZoneInfo.Utc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Mars/Olympus_Mons")]
    public void Unknown_or_blank_names_do_not_resolve(string? name)
    {
        GraphTimeZone.TryFind(name, out var zone).ShouldBeFalse();
        zone.ShouldBe(TimeZoneInfo.Utc);
    }

    [Fact]
    public void Maps_between_windows_and_iana()
    {
        GraphTimeZone.ToWindowsId("Australia/Sydney").ShouldBe("AUS Eastern Standard Time");
        GraphTimeZone.ToIanaId("AUS Eastern Standard Time").ShouldBe("Australia/Sydney");
        GraphTimeZone.ToWindowsId("Europe/London").ShouldBe("GMT Standard Time");
        GraphTimeZone.ToIanaId("GMT Standard Time").ShouldBe("Europe/London");
    }

    [Fact]
    public void Unmappable_names_are_returned_unchanged()
    {
        GraphTimeZone.ToWindowsId("Not/AZone").ShouldBe("Not/AZone");
        GraphTimeZone.ToIanaId("Not A Zone").ShouldBe("Not A Zone");
        Should.Throw<ArgumentException>(() => GraphTimeZone.ToWindowsId(" "));
        Should.Throw<ArgumentException>(() => GraphTimeZone.ToIanaId(""));
    }

    [Fact]
    public void Wall_clock_is_unspecified_local_time_in_the_zone()
    {
        Find("Australia/Sydney", out var sydney);

        var winter = GraphTimeZone.ToWallClock(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), sydney);
        var summer = GraphTimeZone.ToWallClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), sydney);

        winter.ShouldBe(new DateTime(2026, 7, 1, 10, 0, 0));
        winter.Kind.ShouldBe(DateTimeKind.Unspecified);
        summer.ShouldBe(new DateTime(2026, 1, 1, 11, 0, 0));
        Should.Throw<ArgumentNullException>(() => GraphTimeZone.ToWallClock(DateTimeOffset.UnixEpoch, null!));
    }

    [Fact]
    public void Instant_from_wall_clock_respects_dst_with_either_id_form()
    {
        GraphTimeZone.ToInstant(new DateTime(2026, 7, 1, 10, 0, 0), "Australia/Sydney")
            .ShouldBe(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        GraphTimeZone.ToInstant(new DateTime(2026, 1, 1, 11, 0, 0), "AUS Eastern Standard Time")
            .ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Instant_from_wall_clock_defaults_to_utc()
    {
        var wall = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local);

        GraphTimeZone.ToInstant(wall, null).ShouldBe(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        GraphTimeZone.ToInstant(wall, "Nowhere/Special").Offset.ShouldBe(TimeSpan.Zero);
        GraphTimeZone.ToInstant(wall, "UTC").ShouldBe(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Dst_edges_round_trip_through_wall_clock()
    {
        // Sydney leaves DST at 2026-04-05 03:00 local (→ 02:00) and enters it at 2026-10-04 02:00 (→ 03:00).
        Find("Australia/Sydney", out var sydney);
        var beforeEnd = new DateTimeOffset(2026, 4, 4, 15, 30, 0, TimeSpan.Zero); // 02:30 AEDT
        var afterStart = new DateTimeOffset(2026, 10, 3, 16, 30, 0, TimeSpan.Zero); // 03:30 AEDT

        GraphTimeZone.ToWallClock(beforeEnd, sydney).ShouldBe(new DateTime(2026, 4, 5, 2, 30, 0));
        GraphTimeZone.ToWallClock(afterStart, sydney).ShouldBe(new DateTime(2026, 10, 4, 3, 30, 0));
        GraphTimeZone.ToInstant(new DateTime(2026, 10, 4, 3, 30, 0), "Australia/Sydney").ShouldBe(afterStart);
    }

    [Fact]
    public void Dst_gap_and_overlap_resolve_to_standard_offset()
    {
        // 02:30 on 2026-10-04 does not exist in Sydney; 02:30 on 2026-04-05 happens twice.
        // TimeZoneInfo.GetUtcOffset answers the standard offset (+10) for both: documented, deterministic behaviour.
        GraphTimeZone.ToInstant(new DateTime(2026, 10, 4, 2, 30, 0), "Australia/Sydney").Offset.ShouldBe(TimeSpan.FromHours(10));
        GraphTimeZone.ToInstant(new DateTime(2026, 4, 5, 2, 30, 0), "Australia/Sydney").Offset.ShouldBe(TimeSpan.FromHours(10));
    }

    private static void Find(string name, out TimeZoneInfo zone) =>
        GraphTimeZone.TryFind(name, out zone).ShouldBeTrue();
}
