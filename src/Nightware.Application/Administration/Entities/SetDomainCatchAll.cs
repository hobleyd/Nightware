using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>
/// Chooses the user who receives mail sent to addresses no one has on one of an entity's domains (its catch-all),
/// or with no user refuses that mail again. Entity admins may choose for their own entity, as they may give its
/// users aliases on the domain. The user must belong to the entity, be enabled and have their own mailbox: the mail
/// server accepts a catch-all no mailbox holds and bounces every message to it afterwards. Keycloak keeps the
/// choice (the record), then the mail server is told; when that fails, saving again (or Repair) sends it again.
/// </summary>
public sealed class SetDomainCatchAll(IAdminActor actor, IEntityRegistry entities, UserAccess users, IMailDomains mailDomains, IAuditLog audit)
{
    /// <returns>The address the domain's mail to unknown addresses now goes to; null when it is refused.</returns>
    public async Task<Result<MailAddress?>> ExecuteAsync(string entityId, string domain, string? userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result allowed = AdminAuthorization.RequireEntity(actor.Current, entityId);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        if (!MailDomain.TryParse(domain, out MailDomain? mailDomain))
        {
            return Error.Validation("Enter a domain name such as example.com.", "domain");
        }

        MailAddress? address = null;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            Result<MailAddress> target = await TargetAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
            if (!target.IsSuccess)
            {
                return target.Error;
            }

            address = target.Value;
        }
        else
        {
            userId = null;
        }

        Result<Entity> recorded = await entities.SetCatchAllAsync(entityId, mailDomain, userId, cancellationToken).ConfigureAwait(false);
        Result pushed = recorded.IsSuccess
            ? await mailDomains.SetCatchAllAsync(mailDomain, address, cancellationToken).ConfigureAwait(false)
            : Result.Success();
        audit.Record(new AuditEvent("entity.domain.catch-all", actor.Current.UserId, recorded.IsSuccess && pushed.IsSuccess)
        {
            EntityId = entityId,
            TargetUserId = userId,
            Details = new Dictionary<string, string?>
            {
                ["domain"] = mailDomain.Name,
                ["address"] = address?.Value,
                ["mailServer"] = !recorded.IsSuccess ? null : pushed.IsSuccess ? "set" : "failed",
            },
        });
        if (!recorded.IsSuccess)
        {
            return recorded.Error;
        }

        return pushed.IsSuccess
            ? Result<MailAddress?>.Success(address)
            : Error.BackendUnavailable($"The choice for {mailDomain} is saved, but the mail server was not changed ({pushed.Error!.Message}). Save it again.");
    }

    /// <summary>The user's own address, once they are shown to be an enabled member of the entity with a mailbox of their own.</summary>
    private async Task<Result<MailAddress>> TargetAsync(string entityId, string userId, CancellationToken cancellationToken)
    {
        Result<UserDetail> user = await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return user.Error;
        }

        IdentityAccount identity = user.Value.User.Identity;
        if (!string.Equals(user.Value.User.EntityId, entityId, StringComparison.Ordinal))
        {
            return Error.Validation($"{identity.DisplayName} is not a user of this entity.", "userId");
        }

        if (!identity.Enabled)
        {
            return Error.Validation($"{identity.DisplayName} is offboarded; choose someone who reads their mail.", "userId");
        }

        BackendLink mailbox = user.Value.For(BackendKind.Mail);
        if (mailbox.State != LinkState.Linked)
        {
            return Error.Validation(
                mailbox.State == LinkState.Missing
                    ? $"{identity.DisplayName} has no mailbox of their own on the mail server yet, so the mail would bounce."
                    : $"{identity.DisplayName}'s mailbox cannot receive it: {mailbox.Detail}",
                "userId");
        }

        return MailAddress.TryParse(identity.Email, out MailAddress? address)
            ? address
            : Error.Validation($"{identity.DisplayName}'s address {identity.Email} is not one the mail server can deliver to.", "userId");
    }
}
