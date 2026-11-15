using System.Globalization;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>
/// Offset paging for the collections the client pages by <c>$skip</c> or by following a nextLink.
/// The adapter's skip token is the next offset; when present it wins over <c>$skip</c>.
/// </summary>
internal static class Paging
{
    /// <summary>Graph's default page size for messages and folders.</summary>
    public const int DefaultPageSize = 10;

    public static Result<int> Offset(ItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.SkipToken is null)
        {
            return query.Skip ?? 0;
        }

        return int.TryParse(query.SkipToken, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : Error.Validation("The $skiptoken is not valid for this collection.", "$skiptoken");
    }

    public static int Size(ItemQuery query, int max)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Math.Clamp(query.Top ?? DefaultPageSize, 1, Math.Max(1, max));
    }

    /// <summary>The continuation after a page of <paramref name="count"/> items starting at <paramref name="offset"/>, or null at the end.</summary>
    public static string? Next(int offset, int count, int total) =>
        count > 0 && offset + count < total ? (offset + count).ToString(CultureInfo.InvariantCulture) : null;
}
