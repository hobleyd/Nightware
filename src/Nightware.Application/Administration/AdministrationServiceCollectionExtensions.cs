using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Administration.Admins;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Groups;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Application.Administration.Status;
using Nightware.Application.Administration.Users;

namespace Nightware.Application.Administration;

public static class AdministrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the admin console's use cases (Phase 8). Called by the <c>Nightware.Admin</c> host only,
    /// never by <see cref="ApplicationServiceCollectionExtensions.AddApplication"/>: the Graph host has no
    /// administration surface (ADR 0002 D2). The host supplies <see cref="AdministrationOptions"/>,
    /// <see cref="IAdminActor"/> and the ports.
    /// </summary>
    public static IServiceCollection AddAdministrationUseCases(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<UserAccess>();
        services.TryAddScoped<DomainProvisioning>();
        services.TryAddScoped<ProvisioningPlanner>();
        services.TryAddScoped<CredentialIssuer>();
        services.TryAddScoped<DeletionGuard>();
        services.TryAddScoped<DnsTokenVault>();
        services.TryAddScoped<DnsHosts>();

        services.TryAddScoped<ListEntities>();
        services.TryAddScoped<GetEntity>();
        services.TryAddScoped<CreateEntity>();
        services.TryAddScoped<AddEntityDomain>();
        services.TryAddScoped<RemoveEntityDomain>();
        services.TryAddScoped<GetEntityDnsRecords>();
        services.TryAddScoped<SetDomainMailPolicy>();
        services.TryAddScoped<SetEntityDnsToken>();
        services.TryAddScoped<PlanDnsPublication>();
        services.TryAddScoped<RemoveDomainDnsRecords>();
        services.TryAddScoped<PublicAddresses>();
        services.TryAddScoped<AccessHistoryRecorder>();
        services.TryAddScoped<SuggestMailAuthentication>();
        services.TryAddScoped<PublishDnsRecords>();
        services.TryAddScoped<ListEntityAdmins>();
        services.TryAddScoped<GrantEntityAdmin>();
        services.TryAddScoped<RevokeEntityAdmin>();

        services.TryAddScoped<ListGlobalAdmins>();
        services.TryAddScoped<SetGlobalAdmin>();
        services.TryAddScoped<PreferOwnPasskey>();

        services.TryAddScoped<ListUsers>();
        services.TryAddScoped<GetUser>();
        services.TryAddScoped<FindUsers>();
        services.TryAddScoped<GetAttention>();
        services.TryAddScoped<PlanUserProvisioning>();
        services.TryAddScoped<ApplyUserProvisioning>();
        services.TryAddScoped<RepairUser>();
        services.TryAddScoped<SetUserAliases>();
        services.TryAddScoped<ResetUserCredential>();
        services.TryAddScoped<OffboardUser>();
        services.TryAddScoped<ReinstateUser>();
        services.TryAddScoped<GetDeletionPreview>();
        services.TryAddScoped<DeleteUserMail>();
        services.TryAddScoped<DeleteUserGroupwareData>();

        services.TryAddScoped<GroupSync>();
        services.TryAddScoped<GroupAccess>();
        services.TryAddScoped<ListGroups>();
        services.TryAddScoped<GetGroup>();
        services.TryAddScoped<ListUserGroups>();
        services.TryAddScoped<CreateGroup>();
        services.TryAddScoped<UpdateGroup>();
        services.TryAddScoped<AddGroupMember>();
        services.TryAddScoped<RemoveGroupMember>();
        services.TryAddScoped<RepairGroup>();
        services.TryAddScoped<DeleteGroup>();
        services.TryAddScoped<DeleteTeamFolder>();

        services.TryAddScoped<SharedMailboxSync>();
        services.TryAddScoped<SharedMailboxAccess>();
        services.TryAddScoped<ListSharedMailboxes>();
        services.TryAddScoped<GetSharedMailbox>();
        services.TryAddScoped<ListUserSharedMailboxes>();
        services.TryAddScoped<CreateSharedMailbox>();
        services.TryAddScoped<UpdateSharedMailbox>();
        services.TryAddScoped<AddSharedMailboxMember>();
        services.TryAddScoped<RemoveSharedMailboxMember>();
        services.TryAddScoped<RepairSharedMailbox>();
        services.TryAddScoped<DeleteSharedMailbox>();

        services.TryAddScoped<GetSystemStatus>();
        return services;
    }
}
