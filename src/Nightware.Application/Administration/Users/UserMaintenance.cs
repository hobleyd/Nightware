using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Users;

/// <summary>
/// Re-runs provisioning for the parts of an enabled user that are missing: a mailbox, a Nextcloud user, a
/// disabled Nextcloud account. Conflicts are reported and left alone. A mailbox the console deleted is
/// not recreated here (that needs <see cref="ReinstateUser"/>'s explicit choice), and a disabled user is
/// refused: repair must never quietly undo an offboarding.
/// </summary>
public sealed class RepairUser(IAdminActor actor, UserAccess users, IMailAccounts mail, IGroupwareAccounts groupware, IAuditLog audit)
{
    public async Task<Result<UserDetail>> ExecuteAsync(string userId, CancellationToken cancellationToken)
    {
        Result<UserDetail> loaded = await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        IdentityAccount identity = loaded.Value.User.Identity;
        if (!identity.Enabled)
        {
            return Error.Validation("The user is offboarded; reinstate them instead.");
        }

        var changed = new List<string>();
        if (loaded.Value.For(BackendKind.Mail).State == LinkState.Missing && identity.MailDeletedAt is null
            && MailAddress.TryParse(identity.Email, out MailAddress? address))
        {
            Result<MailAccount> created = await mail.CreateAsync(address, identity.Username, cancellationToken).ConfigureAwait(false);
            changed.Add(created.IsSuccess ? "mail:created" : "mail:failed");
        }

        BackendLink cloud = loaded.Value.For(BackendKind.Groupware);
        if (cloud.State == LinkState.Missing && identity.GroupwareDeletedAt is null)
        {
            Result created = await groupware.PreProvisionAsync(identity.Username, identity.Email, identity.DisplayName, cancellationToken).ConfigureAwait(false);
            changed.Add(created.IsSuccess ? "groupware:created" : created.Error.Kind == ErrorKind.Unsupported ? "groupware:pending" : "groupware:failed");
        }
        else if (cloud.State == LinkState.Disabled)
        {
            Result enabled = await groupware.SetEnabledAsync(identity.Username, true, cancellationToken).ConfigureAwait(false);
            changed.Add(enabled.IsSuccess ? "groupware:enabled" : "groupware:failed");
        }

        audit.Record(new AuditEvent("user.repaired", actor.Current.UserId, !changed.Any(static c => c.EndsWith(":failed", StringComparison.Ordinal)))
        {
            TargetUserId = identity.Id,
            EntityId = loaded.Value.User.EntityId,
            Details = new Dictionary<string, string?> { ["changes"] = changed.Count == 0 ? "none" : string.Join(',', changed) },
        });
        return await users.WithLinksAsync(loaded.Value.User, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Replaces a user's secondary addresses in Keycloak and on their mailbox.</summary>
public sealed class SetUserAliases(IAdminActor actor, UserAccess users, IEntityRegistry entities, IIdentityAccounts identities, IMailAccounts mail, IAuditLog audit)
{
    public async Task<Result<UserDetail>> ExecuteAsync(string userId, IReadOnlyList<string> aliases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        Result<UserDetail> loaded = await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        ManagedUser user = loaded.Value.User;
        if (user.EntityId is null)
        {
            return Error.Validation("The user belongs to no entity, so no domain is theirs to use.");
        }

        Result<Entity> entity = await entities.GetAsync(user.EntityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        BackendLink mailbox = loaded.Value.For(BackendKind.Mail);
        var parsed = new List<MailAddress>();
        foreach (string raw in aliases.Where(static a => !string.IsNullOrWhiteSpace(a)))
        {
            if (!MailAddress.TryParse(raw, out MailAddress? alias))
            {
                return Error.Validation($"{raw.Trim()} is not a valid address.", "aliases");
            }

            Result inEntity = AdminAuthorization.RequireAddressInEntity(entity.Value, alias);
            if (!inEntity.IsSuccess)
            {
                return inEntity.Error;
            }

            Result<MailAccount> holder = await mail.FindByAddressAsync(alias.Value, cancellationToken).ConfigureAwait(false);
            if (holder.IsSuccess && holder.Value.Id != mailbox.DownstreamId)
            {
                return Error.Conflict($"The alias {alias} belongs to the mailbox {holder.Value.Address}.", "aliases");
            }

            if (!alias.Matches(user.Identity.Email) && !parsed.Contains(alias))
            {
                parsed.Add(alias);
            }
        }

        Result result = await identities.SetAliasesAsync(userId, parsed, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && mailbox.State == LinkState.Linked)
        {
            result = await mail.SetAliasesAsync(mailbox.DownstreamId!, parsed, cancellationToken).ConfigureAwait(false);
        }

        audit.Record(new AuditEvent("user.aliases.set", actor.Current.UserId, result.IsSuccess)
        {
            TargetUserId = userId,
            EntityId = user.EntityId,
            Details = new Dictionary<string, string?> { ["aliases"] = string.Join(',', parsed.Select(static a => a.Value)) },
        });
        return result.IsSuccess ? await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false) : result.Error;
    }
}

/// <summary>
/// Sends a new set-password e-mail, or generates a new temporary password, for an enabled user. Never for
/// the actor themselves (they use their own account page).
/// </summary>
public sealed class ResetUserCredential(IAdminActor actor, UserAccess users, CredentialIssuer credentials, IAuditLog audit)
{
    public async Task<Result<TemporaryCredential?>> ExecuteAsync(string userId, CredentialDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery == CredentialDelivery.None)
        {
            return Error.Validation("Choose how the user gets their password.", "delivery");
        }

        Result self = AdminAuthorization.RequireNotSelf(actor.Current, userId);
        if (!self.IsSuccess)
        {
            return self.Error;
        }

        Result<ManagedUser> user = await users.LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return user.Error;
        }

        if (!user.Value.Identity.Enabled)
        {
            return Error.Validation("The user is offboarded; reinstate them first.");
        }

        Result<TemporaryCredential?> issued = await credentials.DeliverAsync(userId, delivery, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("user.credential.reset", actor.Current.UserId, issued.IsSuccess)
        {
            TargetUserId = userId,
            EntityId = user.Value.EntityId,
            Details = new Dictionary<string, string?> { ["delivery"] = delivery.ToString() },
        });
        return issued;
    }
}
