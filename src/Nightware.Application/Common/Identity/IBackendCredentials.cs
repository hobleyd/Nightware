using Nightware.Domain.Kernel;

namespace Nightware.Application.Common.Identity;

/// <summary>The backend services Nightware delegates to.</summary>
public enum Backend
{
    /// <summary>Stalwart (JMAP).</summary>
    Mail,
    /// <summary>Nextcloud (CalDAV/CardDAV/WebDAV/OCS).</summary>
    Groupware,
    /// <summary>Keycloak admin API.</summary>
    Directory,
}

/// <summary>How a credential was obtained — useful in logs and for adapters that must adjust headers.</summary>
public enum CredentialStrategy
{
    PassThrough,
    TokenExchange,
    ServiceAccount,
}

/// <summary>An HTTP <c>Authorization</c> value for one backend, plus who it acts as.</summary>
/// <param name="Scheme">Usually <c>Bearer</c>; <c>Basic</c> for a service-account fallback.</param>
/// <param name="Parameter">The credential itself. Never log it.</param>
/// <param name="ActAs">For service-account impersonation, the account to act as (e.g. Stalwart master-user target).</param>
public sealed record BackendCredential(string Scheme, string Parameter, CredentialStrategy Strategy, string? ActAs = null)
{
    public override string ToString() => $"{Scheme} *** ({Strategy}{(ActAs is null ? string.Empty : $" as {ActAs}")})";
}

/// <summary>
/// Supplies the credential an adapter presents to a backend on behalf of the
/// current caller (pass-through, RFC 8693 token exchange, or service account).
/// </summary>
public interface IBackendCredentials
{
    Task<Result<BackendCredential>> ForAsync(Backend backend, CancellationToken cancellationToken);
}
