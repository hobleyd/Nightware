using Nightware.Application.Administration;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Pages.Users;

/// <summary>What each system says after an offboarding, so the admin can be sure every way in is shut.</summary>
public static class OffboardingNotice
{
    public static string For(UserDetail user, TimeSpan accessTokenLifespan)
    {
        ArgumentNullException.ThrowIfNull(user);
        IEnumerable<string> downstream = user.Links.Where(l => l.Backend != BackendKind.Identity).Select(l => (l.Detail?.TrimEnd('.') ?? $"{l.Backend}: {l.State}") + ".");
        return $"{user.User.Identity.DisplayName} is offboarded. Sign-in is disabled and their sessions ended. {string.Join(" ", downstream)} "
            + $"Apps already signed in lose access within {(int)accessTokenLifespan.TotalMinutes} minutes, when their tokens expire. Mail and files are kept.";
    }
}
