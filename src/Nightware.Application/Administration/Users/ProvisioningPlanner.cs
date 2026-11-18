using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Users;

/// <summary>What the new-user wizard submits.</summary>
/// <param name="Username">Null to use the existing Keycloak user's, or else the address's local part.</param>
public sealed record NewUserRequest(
    string EntityId,
    string Address,
    string? Username,
    string? FirstName,
    string? LastName,
    IReadOnlyList<string> Aliases);

/// <summary>A plan with everything resolved while making it, so applying it uses the same facts.</summary>
public sealed record ResolvedPlan(
    ProvisioningPlan Plan,
    Entity Entity,
    MailAddress Address,
    string Username,
    IReadOnlyList<MailAddress> Aliases,
    IdentityAccount? ExistingIdentity,
    BackendLink Mail,
    BackendLink Groupware);

/// <summary>
/// Validates a <see cref="NewUserRequest"/> and decides, per backend, whether to create, link or stop on a
/// conflict (ADR 0002 D3, D10). Reads only; nothing is written.
/// </summary>
public sealed class ProvisioningPlanner(
    IAdminActor actor,
    IEntityRegistry entities,
    IIdentityAccounts identities,
    IAdminScopeSource scopes,
    IMailAccounts mail,
    IGroupwareAccounts groupware,
    AdministrationOptions options)
{
    public async Task<Result<ResolvedPlan>> PlanAsync(NewUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result allowed = AdminAuthorization.RequireEntity(actor.Current, request.EntityId);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        Result<Entity> entity = await entities.GetAsync(request.EntityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        Result<(MailAddress Address, string? Username, IReadOnlyList<MailAddress> Aliases)> parsed = Parse(request, entity.Value, options);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        (MailAddress address, string? requestedUsername, IReadOnlyList<MailAddress> aliases) = parsed.Value;

        Result<IdentityAccount> byEmail = await identities.FindByEmailAsync(address.Value, cancellationToken).ConfigureAwait(false);
        if (Failed(byEmail) is { } emailError)
        {
            return emailError;
        }

        string username = byEmail.IsSuccess && requestedUsername is null ? byEmail.Value.Username : requestedUsername ?? address.LocalPart;
        Result<IdentityAccount> byUsername = await identities.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        if (Failed(byUsername) is { } usernameError)
        {
            return usernameError;
        }

        IReadOnlyList<string> memberships = [];
        if (byEmail.IsSuccess)
        {
            Result<IReadOnlyList<string>> found = await entities.GetMembershipsAsync(byEmail.Value.Id, cancellationToken).ConfigureAwait(false);
            if (!found.IsSuccess)
            {
                return found.Error;
            }

            memberships = found.Value;
        }

        (PlannedStep identityStep, IdentityAccount? existing) =
            LinkStateEvaluator.IdentityStep(request.EntityId, requestedUsername, username, byEmail, byUsername, memberships);
        bool targetIsGlobal = false;
        if (existing is not null)
        {
            // Linking can issue a password, so the actor may link only someone they could manage once
            // linked: never a Keycloak administrator; for an entity admin, also not an administrator
            // elsewhere and not a member of an entity outside their scope.
            Result<AdminScope> scope = await scopes.ResolveAsync(existing.Id, cancellationToken).ConfigureAwait(false);
            if (!scope.IsSuccess)
            {
                return scope.Error;
            }

            IReadOnlyList<string> joined = memberships.Count == 0 ? [request.EntityId] : memberships;
            targetIsGlobal = scope.Value.IsGlobal;
            if (!AdminAuthorization.RequireMayManageUser(actor.Current, joined, scope.Value, options.GlobalAdminsAdministerServers).IsSuccess)
            {
                identityStep = new PlannedStep(BackendKind.Identity, PlannedAction.Conflict, scope.Value.AdministersDirectory
                    ? $"Keycloak user {existing.Username} administers Keycloak; the console does not manage such accounts."
                    : $"Keycloak user {existing.Username} is outside your scope; ask a global administrator.");
                existing = null;
            }
        }

        BackendLink mailLink = LinkStateEvaluator.Mail(address.Value, await mail.FindByAddressAsync(address.Value, cancellationToken).ConfigureAwait(false),
            options.GlobalAdminsAdministerServers && existing is not null && targetIsGlobal);
        PlannedStep mailStep = LinkStateEvaluator.Step(mailLink, $"Create mailbox {address}.");
        if (mailStep.Action != PlannedAction.Conflict)
        {
            mailStep = await CheckAliasesAsync(mailStep, mailLink.DownstreamId, existing?.Id, aliases, cancellationToken).ConfigureAwait(false);
        }

        BackendLink cloudLink = LinkStateEvaluator.Groupware(username, address.Value, await groupware.FindAsync(username, cancellationToken).ConfigureAwait(false),
            options.GlobalAdminsAdministerServers && existing is not null && targetIsGlobal);
        PlannedStep cloudStep = LinkStateEvaluator.Step(cloudLink, $"Create Nextcloud user {username}.");

        var plan = new ProvisioningPlan([identityStep, mailStep, cloudStep]);
        return new ResolvedPlan(plan, entity.Value, address, username, aliases, existing, mailLink, cloudLink);
    }

    private static Result<(MailAddress, string?, IReadOnlyList<MailAddress>)> Parse(NewUserRequest request, Entity entity, AdministrationOptions options)
    {
        if (!MailAddress.TryParse(request.Address, out MailAddress? address))
        {
            return Error.Validation("Enter an address of letters, digits, '.', '_' or '-' on one of the entity's domains.", "address");
        }

        Result inEntity = AdminAuthorization.RequireNotReserved(address, options);
        if (inEntity.IsSuccess)
        {
            inEntity = AdminAuthorization.RequireAddressInEntity(entity, address);
        }

        if (!inEntity.IsSuccess)
        {
            return inEntity.Error;
        }

        string? username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim().ToLowerInvariant();
        if (username is not null && !IsValidUsername(username))
        {
            return Error.Validation("A username is 2–64 letters, digits, '.', '_', '-' or '@'.", "username");
        }

        var aliases = new List<MailAddress>();
        foreach (string raw in request.Aliases.Where(static a => !string.IsNullOrWhiteSpace(a)))
        {
            if (!MailAddress.TryParse(raw, out MailAddress? alias))
            {
                return Error.Validation($"{raw.Trim()} is not a valid address.", "aliases");
            }

            Result aliasInEntity = AdminAuthorization.RequireNotReserved(alias, options);
            if (aliasInEntity.IsSuccess)
            {
                aliasInEntity = AdminAuthorization.RequireAddressInEntity(entity, alias);
            }

            if (!aliasInEntity.IsSuccess)
            {
                return aliasInEntity.Error;
            }

            if (alias != address && !aliases.Contains(alias))
            {
                aliases.Add(alias);
            }
        }

        if (request.FirstName?.Length > 100 || request.LastName?.Length > 100)
        {
            return Error.Validation("Names are at most 100 characters.", "name");
        }

        return (address, username, aliases);
    }

    /// <summary>The same rule Keycloak's default username policy applies, minus characters that need escaping anywhere downstream.</summary>
    internal static bool IsValidUsername(string username) =>
        username.Length is >= 2 and <= 64
        && username.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-' or '@');

    /// <summary>An alias already held by another mailbox would be stolen; that is a conflict, not a link.</summary>
    private async Task<PlannedStep> CheckAliasesAsync(PlannedStep step, string? accountId, string? identityId, IReadOnlyList<MailAddress> aliases, CancellationToken cancellationToken)
    {
        foreach (MailAddress alias in aliases)
        {
            // Someone else's sign-in address would route their mail, set-password links included, here.
            Result<IdentityAccount> owner = await identities.FindByEmailAsync(alias.Value, cancellationToken).ConfigureAwait(false);
            if (owner.IsSuccess && owner.Value.Id != identityId)
            {
                return new PlannedStep(BackendKind.Mail, PlannedAction.Conflict, $"The alias {alias} is the address of Keycloak user {owner.Value.Username}.");
            }

            if (!owner.IsSuccess && owner.Error.Kind != ErrorKind.NotFound)
            {
                return new PlannedStep(BackendKind.Mail, PlannedAction.Conflict, owner.Error.Message);
            }

            Result<MailAccount> holder = await mail.FindByAddressAsync(alias.Value, cancellationToken).ConfigureAwait(false);
            if (holder.IsSuccess && holder.Value.Id != accountId)
            {
                return new PlannedStep(BackendKind.Mail, PlannedAction.Conflict, $"The alias {alias} belongs to the mailbox {holder.Value.Address}.");
            }

            if (!holder.IsSuccess && holder.Error.Kind != ErrorKind.NotFound)
            {
                return new PlannedStep(BackendKind.Mail, PlannedAction.Conflict, holder.Error.Message);
            }
        }

        return step;
    }

    private static Error? Failed(Result<IdentityAccount> lookup) =>
        lookup.IsSuccess || lookup.Error.Kind == ErrorKind.NotFound ? null : lookup.Error;
}
