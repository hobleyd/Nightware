using Nightware.Application.Directory;
using Nightware.Domain.Chats;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Chats.Talk;

/// <summary>
/// Talk's people as directory identities and back (ADR 0009 D2), for one request: a Nextcloud uid becomes the
/// directory user with that account name; one the directory doesn't know (a guest, a bot, a federated or deleted user)
/// keeps Talk's display name and no id. Load the uids a batch names first (<see cref="LoadAsync"/>); <see cref="Identify"/>
/// is then synchronous, which is what <see cref="TalkMessageCodec"/> needs.
/// </summary>
internal sealed class TalkIdentities(IUserAccountNames names)
{
    private readonly Dictionary<string, DirectoryUser?> _users = new(StringComparer.Ordinal);

    /// <summary>The map when no directory is composed: nobody is known, and nobody can be named.</summary>
    public static IUserAccountNames NoDirectory { get; } = new NoAccountNames();

    /// <summary>Resolves every uid not yet known. Only the directory being unreachable fails; an unknown uid is fine.</summary>
    public async Task<Result> LoadAsync(IEnumerable<string> accountNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountNames);
        foreach (var accountName in accountNames.Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal).ToList())
        {
            if (_users.ContainsKey(accountName))
            {
                continue;
            }

            var user = await names.UserOfAccountAsync(accountName, cancellationToken).ConfigureAwait(false);
            if (!user.IsSuccess && user.Error.Kind != ErrorKind.NotFound)
            {
                return user.Error;
            }

            _users[accountName] = user.IsSuccess ? user.Value : null;
        }

        return Result.Success();
    }

    /// <summary>The directory user behind a uid (after <see cref="LoadAsync"/>), or null.</summary>
    public DirectoryUser? UserOf(string accountName) => _users.GetValueOrDefault(accountName);

    public ChatIdentity Identify(TalkActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.IsUser && UserOf(actor.Id) is { } user
            ? new ChatIdentity(user.Id, user.DisplayName, ChatIdentityKind.User)
            : TalkIdentityDefaults.Anonymous(actor);
    }

    /// <summary>A directory id (or UPN, or address) as the uid Talk knows the person by; NotFound for nobody in the directory.</summary>
    public async Task<Result<string>> AccountNameOfAsync(string user, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        var accountName = await names.AccountNameOfAsync(user, cancellationToken).ConfigureAwait(false);
        return accountName.IsSuccess || accountName.Error.Kind != ErrorKind.NotFound
            ? accountName
            : Error.NotFound($"No user '{user}' in the directory.", "user");
    }

    private sealed class NoAccountNames : IUserAccountNames
    {
        public Task<Result<string>> AccountNameOfAsync(string user, CancellationToken cancellationToken) =>
            Task.FromResult<Result<string>>(Error.Unsupported("No directory is configured to name people in chats."));

        public Task<Result<DirectoryUser>> UserOfAccountAsync(string accountName, CancellationToken cancellationToken) =>
            Task.FromResult<Result<DirectoryUser>>(Error.NotFound("No directory is configured."));
    }
}
