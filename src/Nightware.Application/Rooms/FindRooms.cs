using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Rooms;

/// <summary>
/// Graph beta <c>GET me/findRooms</c>: every room as a name and address, unpaged. Walks the directory's
/// pages; the page cap guards against a backend that keeps returning continuations.
/// </summary>
public sealed class FindRooms(IRoomDirectory rooms)
{
    internal const int PageSize = 500;
    internal const int MaxPages = 20;

    public async Task<Result<IReadOnlyList<EmailAddress>>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var found = new List<EmailAddress>();
        string? skipToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await rooms.ListAsync(new ItemQuery { Top = PageSize, SkipToken = skipToken }, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return result.Error;
            }

            found.AddRange(result.Value.Items.Select(r => new EmailAddress(r.EmailAddress, r.DisplayName)));
            skipToken = result.Value.SkipToken;
            if (skipToken is null)
            {
                break;
            }
        }

        return found;
    }
}
