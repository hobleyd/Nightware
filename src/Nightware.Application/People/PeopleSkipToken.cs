using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Nightware.Application.People;

/// <summary>
/// The <c>$skiptoken</c> of a <c>/people</c> page: the offset into the ranking and the instant the walk
/// ranked against. Freezing the instant keeps the order (recency decays with time) identical on every
/// page of one walk, so people neither repeat nor go missing between pages.
/// </summary>
internal readonly record struct PeopleSkipToken(int Offset, DateTimeOffset RankedAt)
{
    private const string Version = "p1";

    public override string ToString() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}:{Offset}:{RankedAt.ToUnixTimeSeconds()}")));

    public static bool TryParse(string value, out PeopleSkipToken token)
    {
        token = default;
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = text.Split(':');
        if (parts.Length != 3
            || parts[0] != Version
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        token = new PeopleSkipToken(offset, DateTimeOffset.FromUnixTimeSeconds(seconds));
        return true;
    }
}
