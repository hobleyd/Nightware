using Nightware.Domain.Administration;

namespace Nightware.Application.Administration.Users;

/// <summary>A user's state across every system, reduced to the one fact an admin needs first.</summary>
public enum UserStatus
{
    /// <summary>Signs in, and every system agrees.</summary>
    Active,

    /// <summary>Signs in, but a system is missing their account or could not be checked: Repair.</summary>
    NeedsRepair,

    /// <summary>Signs in, but a system holds an account that disagrees: never merged automatically.</summary>
    Conflict,

    /// <summary>Sign-in is disabled, but a mailbox or Nextcloud account may still let them in.</summary>
    OffboardingIncomplete,

    /// <summary>Sign-in is disabled in every system.</summary>
    Offboarded,
}

/// <summary>Derives <see cref="UserStatus"/> once, so every page states it the same way.</summary>
public static class UserStatuses
{
    public static UserStatus Of(UserDetail user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.User.Identity.Enabled)
        {
            return OffboardUser.IsIncomplete(user) ? UserStatus.OffboardingIncomplete : UserStatus.Offboarded;
        }

        if (user.Links.Any(static l => l.State == LinkState.Conflict))
        {
            return UserStatus.Conflict;
        }

        // A list read from Keycloak alone (very large entities) carries only the identity link: nothing to doubt.
        return user.Links.Any(static l => l.State is LinkState.Missing or LinkState.Error)
            ? UserStatus.NeedsRepair
            : UserStatus.Active;
    }
}
