using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Core.Session;

/// <summary>The current caller's backend credential and the session it opens.</summary>
internal sealed record JmapConnection(BackendCredential Credential, JmapSession Session);

/// <summary>
/// Scoped (one per Graph request): obtains the caller's credential from
/// <see cref="IBackendCredentials"/> — never from <see cref="IUserContext.BearerToken"/> —
/// once per scope, and the session for it from the shared cache.
/// </summary>
internal sealed class JmapConnectionProvider(IBackendCredentials credentials, IJmapSessionProvider sessions)
{
    private BackendCredential? _credential;

    public async Task<Result<JmapConnection>> ConnectAsync(CancellationToken cancellationToken)
    {
        var credential = await GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (!credential.IsSuccess)
        {
            return credential.Error;
        }

        var session = await sessions.GetAsync(credential.Value, cancellationToken).ConfigureAwait(false);
        return session.Map(s => new JmapConnection(credential.Value, s));
    }

    /// <summary>Forgets the scope's credential and its cached session (after a 401).</summary>
    public void Reject(BackendCredential credential)
    {
        sessions.Invalidate(credential);
        _credential = null;
    }

    public void ObserveSessionState(JmapConnection connection, string? sessionState)
    {
        ArgumentNullException.ThrowIfNull(connection);
        sessions.ObserveSessionState(connection.Credential, connection.Session, sessionState);
    }

    private async Task<Result<BackendCredential>> GetCredentialAsync(CancellationToken cancellationToken)
    {
        if (_credential is not null)
        {
            return _credential;
        }

        var result = await credentials.ForAsync(Backend.Mail, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _credential = result.Value;
        }

        return result;
    }
}
