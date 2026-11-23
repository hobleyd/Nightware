using Nightware.Domain.Kernel;
using Nightware.Domain.Presence;

namespace Nightware.Application.Presence;

/// <summary>
/// Nextcloud user status, as the caller (ADR 0009 D6). Nextcloud lets a user set only their own status, so every write
/// is the caller's.
/// </summary>
public interface IUserStatusStore
{
    /// <summary>The caller's own status, with its predefined message id; <see cref="UserStatus.None"/> when there is none.</summary>
    Task<Result<UserStatus>> GetOwnAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Another user's public status (never <see cref="UserStatusType.Invisible"/>); <see cref="UserStatus.None"/> when
    /// they have none.
    /// </summary>
    /// <param name="accountName">The Nextcloud uid.</param>
    Task<Result<UserStatus>> GetAsync(string accountName, CancellationToken cancellationToken);

    /// <summary>Sets the caller's user-defined status type, leaving the message as it is.</summary>
    Task<Result> SetStatusAsync(UserStatusType type, CancellationToken cancellationToken);

    /// <summary>Sets one of Nextcloud's predefined messages (<see cref="PredefinedMessages"/>).</summary>
    Task<Result> SetPredefinedMessageAsync(string messageId, DateTimeOffset? clearAt, CancellationToken cancellationToken);

    Task<Result> SetCustomMessageAsync(string message, DateTimeOffset? clearAt, CancellationToken cancellationToken);

    Task<Result> ClearMessageAsync(CancellationToken cancellationToken);
}
