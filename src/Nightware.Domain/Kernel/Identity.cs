namespace Nightware.Domain.Kernel;

/// <summary>The authenticated caller.</summary>
public sealed record Principal(string Id, string Upn, string Mail, string DisplayName);

/// <summary>
/// The mailbox an operation targets, already resolved from <c>/me</c> or
/// <c>/users/{id|upn}</c> to a canonical SMTP address.
/// </summary>
public readonly record struct MailboxRef(string Address)
{
    public override string ToString() => Address;
}

/// <summary>A display name and SMTP address (Graph <c>emailAddress</c>).</summary>
public sealed record EmailAddress(string Address, string? Name = null);
