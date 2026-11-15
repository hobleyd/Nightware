using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Features.Mail.Write.Dto;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Mail.Write;

public sealed class MapiPropertiesTests
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static MessageDto With(bool? isRead, params (string Id, string Value)[] properties) => new()
    {
        IsRead = isRead,
        SingleValueExtendedProperties = [.. properties.Select(p => new SingleValueExtendedPropertyDto { Id = p.Id, Value = p.Value })],
    };

    [Theory]
    [InlineData("SystemTime 0x0E06", 0x0E06)]
    [InlineData("SystemTime 0xE06", 0x0E06)]
    [InlineData("systemtime 0X0e06", 0x0E06)]
    [InlineData("SystemTime  0x0039", 0x0039)]
    [InlineData("Integer 0x0E07", 0x0E07)]
    public void Tag_parses_type_and_hex_tag(string id, int expected)
    {
        MapiProperties.Tag(id).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("String {00020329-0000-0000-C000-000000000046} Name Keywords")]
    [InlineData("Integer 0E07")]
    [InlineData("Integer 0xZZ")]
    [InlineData("0x0E07")]
    public void Tag_is_null_for_other_forms(string? id)
    {
        MapiProperties.Tag(id).ShouldBeNull();
    }

    [Fact]
    public void Migration_properties_set_dates_and_read_state()
    {
        var options = MapiProperties.ToImportOptions(With(false,
            ("SystemTime 0x0E06", "2026-03-04T05:06:07.000Z"),
            ("SystemTime 0x0039", "2026-03-04T05:06:00Z"),
            ("Integer 0x0E07", "1"))).Value;

        options.ReceivedAt.ShouldBe(Instant);
        options.SentAt.ShouldBe(Instant.AddSeconds(-7));
        options.IsRead.ShouldBeTrue();
        options.IsDraft.ShouldBeFalse();
    }

    [Theory]
    [InlineData("0", false, false)]
    [InlineData("1", true, false)]
    [InlineData("8", false, true)]
    [InlineData("9", true, true)]
    public void Flags_bits_are_read_and_unsent(string flags, bool isRead, bool isDraft)
    {
        var options = MapiProperties.ToImportOptions(With(true, ("Integer 0x0E07", flags))).Value;

        options.IsRead.ShouldBe(isRead);
        options.IsDraft.ShouldBe(isDraft);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void Without_flags_the_message_is_a_draft_read_as_isRead_says(bool? isRead)
    {
        var options = MapiProperties.ToImportOptions(With(isRead)).Value;

        options.IsDraft.ShouldBeTrue();
        options.IsRead.ShouldBe(isRead == true);
        options.ReceivedAt.ShouldBeNull();
        options.SentAt.ShouldBeNull();
    }

    [Fact]
    public void Times_with_an_offset_are_normalised_to_utc()
    {
        var options = MapiProperties.ToImportOptions(With(null, ("SystemTime 0x0E06", "2026-03-04T15:06:07.123+10:00"))).Value;

        options.ReceivedAt.ShouldBe(Instant.AddMilliseconds(123));
        options.ReceivedAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        MapiProperties.ToImportOptions(With(true, ("String 0x1000", "whatever"), ("String {guid} Name X", "y"))).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("SystemTime 0x0E06", "yesterday")]
    [InlineData("SystemTime 0x0039", "")]
    [InlineData("Integer 0x0E07", "one")]
    [InlineData("Integer 0x0E07", "1.5")]
    public void Invalid_values_are_a_validation_error(string id, string value)
    {
        var options = MapiProperties.ToImportOptions(With(true, (id, value)));

        options.Error!.Kind.ShouldBe(ErrorKind.Validation);
        options.Error.Target.ShouldBe("singleValueExtendedProperties");
    }
}
