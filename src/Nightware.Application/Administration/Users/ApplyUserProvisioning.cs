using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Users;

/// <param name="Credential">The generated temporary password, to show once; null for other deliveries.</param>
/// <param name="CredentialError">Why the credential could not be delivered, when it could not.</param>
public sealed record ProvisioningResult(
    IdentityAccount User,
    IReadOnlyList<BackendLink> Links,
    TemporaryCredential? Credential,
    string? CredentialError);

/// <summary>
/// Applies a new-user request (ADR 0002 D10): recomputes the plan (a plan from the form is never trusted),
/// refuses on any conflict, then ensures Keycloak user → entity membership → mailbox → Nextcloud user →
/// credential delivery. Each step is idempotent. Nothing is rolled back: a later failure is reported per
/// backend and fixed with <see cref="RepairUser"/>, never by deleting an account that already existed.
/// </summary>
public sealed class ApplyUserProvisioning(
    IAdminActor actor,
    ProvisioningPlanner planner,
    IEntityRegistry entities,
    IIdentityAccounts identities,
    IMailAccounts mail,
    IGroupwareAccounts groupware,
    CredentialIssuer credentials,
    IAuditLog audit)
{
    public async Task<Result<ProvisioningResult>> ExecuteAsync(NewUserRequest request, CredentialDelivery delivery, CancellationToken cancellationToken)
    {
        Result<ResolvedPlan> planned = await planner.PlanAsync(request, cancellationToken).ConfigureAwait(false);
        if (!planned.IsSuccess)
        {
            return planned.Error;
        }

        ResolvedPlan p = planned.Value;
        if (p.ExistingIdentity is { } linked)
        {
            if (string.Equals(linked.Id, actor.Current.UserId, StringComparison.Ordinal))
            {
                return Error.Forbidden("You cannot link your own account.");
            }

            if (delivery != CredentialDelivery.None)
            {
                // Linking never changes someone's password behind the plan's back; that is a separate,
                // audited action on the user's page.
                return Error.Validation("This person already has a Keycloak account. Choose 'None' for the password; reset it from their page if needed.", "delivery");
            }
        }

        if (p.Plan.HasConflict)
        {
            PlannedStep first = p.Plan.Steps.First(static s => s.Action == PlannedAction.Conflict);
            return Error.Conflict(first.Reason, first.Backend.ToString());
        }

        Result<IdentityAccount> identity = p.ExistingIdentity is { } existing
            ? existing
            : await identities.CreateAsync(new NewIdentity(p.Username, p.Address, Clean(request.FirstName), Clean(request.LastName), []), cancellationToken).ConfigureAwait(false);
        if (!identity.IsSuccess)
        {
            Record(null, p, false, []);
            return identity.Error;
        }

        IdentityAccount user = identity.Value;
        Result step = await entities.AddMemberAsync(p.Entity.Id, user.Id, cancellationToken).ConfigureAwait(false);
        if (step.IsSuccess && p.Aliases.Count > 0)
        {
            step = await identities.SetAliasesAsync(user.Id, p.Aliases, cancellationToken).ConfigureAwait(false);
        }

        BackendLink identityLink = step.IsSuccess
            ? LinkStateEvaluator.Identity(user)
            : new BackendLink(BackendKind.Identity, LinkState.Error, step.Error.Message, user.Id);

        BackendLink mailLink = await EnsureMailboxAsync(p, cancellationToken).ConfigureAwait(false);
        BackendLink cloudLink = await EnsureGroupwareAsync(p, user, cancellationToken).ConfigureAwait(false);

        Result<TemporaryCredential?> credential = await credentials.DeliverAsync(user.Id, delivery, cancellationToken).ConfigureAwait(false);
        BackendLink[] links = [identityLink, mailLink, cloudLink];
        Record(user.Id, p, links.All(static l => l.State is LinkState.Linked or LinkState.Pending) && credential.IsSuccess, links, delivery);
        return new ProvisioningResult(
            user,
            links,
            credential.IsSuccess ? credential.Value : null,
            credential.IsSuccess ? null : credential.Error.Message);
    }

    private async Task<BackendLink> EnsureMailboxAsync(ResolvedPlan p, CancellationToken cancellationToken)
    {
        BackendLink link = p.Mail;
        if (link.State == LinkState.Missing)
        {
            Result<MailAccount> created = await mail.CreateAsync(p.Address, p.Username, cancellationToken).ConfigureAwait(false);
            link = created.IsSuccess
                ? new BackendLink(BackendKind.Mail, LinkState.Linked, $"Mailbox {created.Value.Address}", created.Value.Id)
                : new BackendLink(BackendKind.Mail, LinkState.Error, created.Error.Message);
        }

        if (link.State == LinkState.Disabled)
        {
            Result enabled = await mail.SetEnabledAsync(link.DownstreamId!, true, cancellationToken).ConfigureAwait(false);
            link = enabled.IsSuccess
                ? link with { State = LinkState.Linked, Detail = $"Mailbox {p.Address}" }
                : link with { State = LinkState.Error, Detail = enabled.Error.Message };
        }

        if (link.State == LinkState.Linked && p.Aliases.Count > 0)
        {
            Result aliased = await mail.SetAliasesAsync(link.DownstreamId!, p.Aliases, cancellationToken).ConfigureAwait(false);
            if (!aliased.IsSuccess)
            {
                link = link with { State = LinkState.Error, Detail = aliased.Error.Message };
            }
        }

        return link;
    }

    private async Task<BackendLink> EnsureGroupwareAsync(ResolvedPlan p, IdentityAccount user, CancellationToken cancellationToken)
    {
        BackendLink link = p.Groupware;
        if (link.State == LinkState.Missing)
        {
            Result created = await groupware.PreProvisionAsync(p.Username, p.Address.Value, user.DisplayName, cancellationToken).ConfigureAwait(false);
            return created.IsSuccess
                ? new BackendLink(BackendKind.Groupware, LinkState.Linked, $"Nextcloud user {p.Username}", p.Username)
                : created.Error.Kind == ErrorKind.Unsupported
                    ? new BackendLink(BackendKind.Groupware, LinkState.Pending, "Created at first sign-in.")
                    : new BackendLink(BackendKind.Groupware, LinkState.Error, created.Error.Message);
        }

        if (link.State == LinkState.Disabled)
        {
            Result enabled = await groupware.SetEnabledAsync(p.Username, true, cancellationToken).ConfigureAwait(false);
            return enabled.IsSuccess
                ? link with { State = LinkState.Linked, Detail = $"Nextcloud user {p.Username}" }
                : link with { State = LinkState.Error, Detail = enabled.Error.Message };
        }

        return link;
    }

    private void Record(string? userId, ResolvedPlan p, bool succeeded, IReadOnlyList<BackendLink> links, CredentialDelivery delivery = CredentialDelivery.None)
    {
        var details = new Dictionary<string, string?>
        {
            ["address"] = p.Address.Value,
            ["username"] = p.Username,
            ["identityAction"] = p.Plan.For(BackendKind.Identity).Action.ToString(),
            ["credential"] = delivery.ToString(),
        };
        foreach (BackendLink link in links)
        {
            details[link.Backend.ToString().ToLowerInvariant()] = link.State.ToString();
        }

        audit.Record(new AuditEvent("user.provisioned", actor.Current.UserId, succeeded) { TargetUserId = userId, EntityId = p.Entity.Id, Details = details });
    }

    private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
