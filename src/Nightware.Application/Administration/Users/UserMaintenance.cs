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

        BackendLink mailbox = loaded.Value.For(BackendKind.Mail);
        if (mailbox.State == LinkState.Disabled)
        {
            Result enabled = await mail.SetEnabledAsync(mailbox.DownstreamId!, true, cancellationToken).ConfigureAwait(false);
            changed.Add(enabled.IsSuccess ? "mail:enabled" : "mail:failed");
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

/// <summary>
/// Marks a user's email verified in Keycloak. The façade refuses tokens without <c>email_verified</c> (the email names
/// the mailbox), so until this a person created outside the console cannot use their mail apps.
/// </summary>
public sealed class VerifyUserEmail(IAdminActor actor, UserAccess users, IIdentityAccounts identities, IAuditLog audit)
{
    public async Task<Result<UserDetail>> ExecuteAsync(string userId, CancellationToken cancellationToken)
    {
        Result<UserDetail> loaded = await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result result = await identities.MarkEmailVerifiedAsync(userId, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("user.email.verified", actor.Current.UserId, result.IsSuccess)
        {
            TargetUserId = userId,
            EntityId = loaded.Value.User.EntityId,
        });
        return result.IsSuccess ? await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false) : result.Error;
    }
}

/// <summary>
/// Replaces a user's Graph profile fields (job title, department, company, office, phones) in Keycloak, where the
/// façade's directory reads them. Values are trimmed and capped as the realm's user profile declares them.
/// </summary>
public sealed class SetUserProfile(IAdminActor actor, UserAccess users, IIdentityAccounts identities, IAuditLog audit)
{
    internal const int MaxLength = 255;

    public async Task<Result<UserDetail>> ExecuteAsync(string userId, UserProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Result<UserDetail> loaded = await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var fields = new[] { profile.JobTitle, profile.Department, profile.CompanyName, profile.OfficeLocation, profile.MobilePhone }.Concat(profile.BusinessPhones);
        if (fields.Any(static f => f is { Length: > MaxLength }))
        {
            return Error.Validation($"Each profile field can be at most {MaxLength} characters.", "profile");
        }

        Result result = await identities.SetProfileAsync(userId, profile, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("user.profile.set", actor.Current.UserId, result.IsSuccess)
        {
            TargetUserId = userId,
            EntityId = loaded.Value.User.EntityId,
        });
        return result.IsSuccess ? await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false) : result.Error;
    }
}

/// <summary>
/// Sets or removes a user's manager (phase 21), the directory's <c>manager</c> and <c>directReports</c>. The manager is
/// named by address or username and must be another member of the user's entity; a choice that would make a loop
/// (the user managing their own manager, however far up) is refused.
/// </summary>
public sealed class SetUserManager(IAdminActor actor, UserAccess users, IIdentityAccounts identities, IEntityRegistry entities, IAuditLog audit)
{
    /// <summary>How far up the chain a loop is looked for; a deeper chain is refused rather than walked for ever.</summary>
    internal const int MaxChain = 100;

    /// <param name="manager">The manager's address or username; blank removes the manager.</param>
    public async Task<Result<UserDetail>> ExecuteAsync(string userId, string? manager, CancellationToken cancellationToken)
    {
        Result<UserDetail> loaded = await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        ManagedUser user = loaded.Value.User;
        string? managerId = null;
        if (!string.IsNullOrWhiteSpace(manager))
        {
            Result<string> chosen = await ChooseAsync(user, manager.Trim(), cancellationToken).ConfigureAwait(false);
            if (!chosen.IsSuccess)
            {
                return chosen.Error;
            }

            managerId = chosen.Value;
        }

        Result result = await identities.SetManagerAsync(userId, managerId, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("user.manager.set", actor.Current.UserId, result.IsSuccess)
        {
            TargetUserId = userId,
            EntityId = user.EntityId,
        });
        return result.IsSuccess ? await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false) : result.Error;
    }

    private async Task<Result<string>> ChooseAsync(ManagedUser user, string name, CancellationToken cancellationToken)
    {
        Result<IdentityAccount> found = name.Contains('@', StringComparison.Ordinal)
            ? await identities.FindByEmailAsync(name, cancellationToken).ConfigureAwait(false)
            : await identities.FindByUsernameAsync(name, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error.Kind == ErrorKind.NotFound ? Error.Validation($"Nobody is called {name}.", "manager") : found.Error;
        }

        IdentityAccount candidate = found.Value;
        if (string.Equals(candidate.Id, user.Identity.Id, StringComparison.Ordinal))
        {
            return Error.Validation("Nobody can be their own manager.", "manager");
        }

        if (user.EntityId is null)
        {
            return Error.Validation("The user belongs to no entity, so nobody can be their manager.", "manager");
        }

        Result<IReadOnlyList<string>> memberships = await entities.GetMembershipsAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
        if (!memberships.IsSuccess)
        {
            return memberships.Error;
        }

        if (!memberships.Value.Contains(user.EntityId, StringComparer.Ordinal))
        {
            return Error.Validation($"{name} isn't in the same entity.", "manager");
        }

        // Walk up from the candidate: meeting the user means they would manage their own manager.
        string? next = candidate.ManagerId;
        for (int step = 0; next is not null; step++)
        {
            if (string.Equals(next, user.Identity.Id, StringComparison.Ordinal))
            {
                return Error.Validation($"{user.Identity.DisplayName} already manages {name}, directly or further up, so that would make a loop.", "manager");
            }

            if (step == MaxChain)
            {
                return Error.Validation($"{name}'s chain of managers is too long to check.", "manager");
            }

            Result<IdentityAccount> above = await identities.GetAsync(next, cancellationToken).ConfigureAwait(false);
            if (!above.IsSuccess)
            {
                if (above.Error.Kind == ErrorKind.NotFound)
                {
                    break;
                }

                return above.Error;
            }

            next = above.Value.ManagerId;
        }

        return candidate.Id;
    }
}

/// <summary>Replaces a user's secondary addresses in Keycloak and on their mailbox.</summary>
public sealed class SetUserAliases(IAdminActor actor, UserAccess users, IEntityRegistry entities, IIdentityAccounts identities, IMailAccounts mail, AdministrationOptions options, IAuditLog audit)
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

            Result inEntity = AdminAuthorization.RequireNotReserved(alias, options);
            if (inEntity.IsSuccess)
            {
                inEntity = AdminAuthorization.RequireAddressInEntity(entity.Value, alias);
            }

            if (!inEntity.IsSuccess)
            {
                return inEntity.Error;
            }

            Result<IdentityAccount> owner = await identities.FindByEmailAsync(alias.Value, cancellationToken).ConfigureAwait(false);
            if (owner.IsSuccess && owner.Value.Id != userId)
            {
                return Error.Conflict($"The alias {alias} is the address of Keycloak user {owner.Value.Username}.", "aliases");
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
