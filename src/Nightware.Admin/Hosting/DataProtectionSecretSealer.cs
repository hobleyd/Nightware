using System.Security.Cryptography;

using Microsoft.AspNetCore.DataProtection;

using Nightware.Application.Administration;

namespace Nightware.Admin.Hosting;

/// <summary>
/// <see cref="ISecretSealer"/> over ASP.NET Data Protection: authenticated encryption with the console's key ring
/// (the <c>admin-keys</c> volume), so the sealed text kept in Keycloak is useless without that volume. Each purpose
/// gets its own protector; text sealed for one purpose does not open under another.
/// </summary>
public sealed class DataProtectionSecretSealer(IDataProtectionProvider provider) : ISecretSealer
{
    private const string RootPurpose = "Nightware.Admin.Secrets";

    public string Seal(string purpose, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(secret);
        return provider.CreateProtector(RootPurpose, purpose).Protect(secret);
    }

    public bool TryUnseal(string purpose, string sealedSecret, out string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        try
        {
            secret = provider.CreateProtector(RootPurpose, purpose).Unprotect(sealedSecret);
            return true;
        }
        catch (CryptographicException)
        {
            secret = string.Empty;
            return false;
        }
    }
}
