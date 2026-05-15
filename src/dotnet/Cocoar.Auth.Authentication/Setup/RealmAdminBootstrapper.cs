using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;
using Cocoar.Auth.Authorization.Services;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Authentication.Setup;

/// <summary>
/// Atomic creation of the very first admin user inside a realm — used in three
/// places that all need exactly the same state-write:
/// <list type="bullet">
///   <item><description>Recovery-CLI <c>bootstrap-admin --password</c> (Direct mode)</description></item>
///   <item><description>SPA bootstrap form <c>POST /api/account/bootstrap-admin</c> (consumes a PendingAdminInvite)</description></item>
///   <item><description>Future migration / repair tooling that needs to seed an admin</description></item>
/// </list>
///
/// <para>What "atomic" means here: in one Marten transaction we (a) create the
/// <c>ApplicationUser</c> via <see cref="UserManager{T}"/> (which appends
/// UserCreatedEvent + UserUserNameChangedEvent + UserPasswordChangedEvent), (b)
/// seed the three default roles (System Admin / User Manager / Viewer) if they
/// don't yet exist in this realm, (c) create the <c>Administratoren</c> group
/// with the user as sole member and the System Admin role attached. If any
/// step fails, the whole transaction rolls back — no half-bootstrapped realms.</para>
///
/// <para>The seeded structure mirrors what the legacy <c>POST /api/setup/create-admin</c>
/// endpoint produced, so existing realms keep the same shape.</para>
///
/// <para>Tenant-scoping: the <see cref="IDocumentSession"/> resolved by DI is
/// tenant-aware via <c>TenantedSessionFactory</c>. Callers must establish the
/// tenant context (HttpContext.Items["TenantId"] or
/// <c>TenantContext.Enter(slug)</c>) BEFORE resolving this service from a
/// scope.</para>
/// </summary>
public interface IRealmAdminBootstrapper
{
    /// <summary>
    /// Create an admin user with a known password and add them to the
    /// <c>Administratoren</c> group. Returns <see cref="Error"/> on
    /// validation failures from <see cref="UserManager{T}"/> (password
    /// rules, duplicate username) or domain conflicts.
    /// </summary>
    Task<ErrorOr<BootstrappedAdmin>> BootstrapDirectAsync(
        string userName,
        string password,
        string email,
        string? firstname,
        string? lastname,
        CancellationToken ct = default);
}

public sealed record BootstrappedAdmin(Guid UserId, string UserName, string Email);

public sealed class RealmAdminBootstrapper(
    IDocumentSession session,
    UserManager<ApplicationUser> userManager) : IRealmAdminBootstrapper
{
    public async Task<ErrorOr<BootstrappedAdmin>> BootstrapDirectAsync(
        string userName,
        string password,
        string email,
        string? firstname,
        string? lastname,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return Error.Validation("Bootstrap.UserNameRequired", "Username is required.");
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return Error.Validation("Bootstrap.EmailRequired", "A valid email address is required.");

        var normalizedUserName = userName.Trim().ToLowerInvariant();

        // UserManager.CreateAsync validates against the configured Identity
        // PasswordOptions (length, digit, uppercase, special). A weak password
        // is rejected here — same treatment for CLI Direct-Mode and for the
        // Invite-Mode bootstrap endpoint, no privilege bypass.
        var appUser = new ApplicationUser(normalizedUserName, email)
        {
            Id = Guid.NewGuid(),
            Firstname = firstname,
            Lastname = lastname,
            IsActive = true,
        };

        var createResult = await userManager.CreateAsync(appUser, password);
        if (!createResult.Succeeded)
        {
            return Error.Validation(
                "Bootstrap.UserCreationFailed",
                string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        await SeedDefaultRolesAndAdminGroupAsync(appUser.Id, ct);
        await session.SaveChangesAsync(ct);

        return new BootstrappedAdmin(appUser.Id, appUser.UserName!, email);
    }

    /// <summary>
    /// Idempotent seed of the three default roles + the
    /// <c>Administratoren</c> group containing the new user. Called from
    /// <see cref="BootstrapDirectAsync"/> AND will be called from the
    /// future Invite-Mode endpoint.
    /// </summary>
    private async Task SeedDefaultRolesAndAdminGroupAsync(Guid userId, CancellationToken ct)
    {
        // System Admin role — the realm:admin bypass. Idempotent: skip if
        // a role with the IsRealmAdmin flag already exists (a re-bootstrap
        // shouldn't duplicate the row).
        var existingAdminRole = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted && r.IsRealmAdmin)
            .FirstOrDefaultAsync(ct);

        Guid adminRoleId;
        if (existingAdminRole is null)
        {
            // PermissionRoleProjection (inline) builds the doc from
            // PermissionRoleCreatedEvent. Direct session.Store(adminRole)
            // would conflict with the projection's own write under Marten
            // 8.34+ optimistic-concurrency detection — emit the event only.
            var adminRole = new PermissionRole
            {
                Id = Guid.NewGuid(),
                Name = "System Admin",
                Description = "Full system access — bypasses every permission check.",
                AppId = null,
                IsRealmAdmin = true,
                PermissionIds = [],
            };
            session.Events.StartStream(adminRole.Id,
                new PermissionRoleCreatedEvent(
                    adminRole.Id, adminRole.Name, adminRole.Description,
                    adminRole.AppId, adminRole.IsRealmAdmin, adminRole.PermissionIds));
            adminRoleId = adminRole.Id;

            // Two starter roles (User Manager + Viewer) — FK into the
            // cocoar-auth App's seeded catalog. Look up the App (it must
            // exist in this tenant; AppRealmSeeder runs at realm-provisioning
            // time, before any admin bootstrap).
            var cocoarAuthApp = await session.Query<App>()
                .FirstOrDefaultAsync(a => a.Slug == AppSlugs.CocoarAuth && !a.IsDeleted, ct)
                ?? throw new InvalidOperationException(
                    "cocoar-auth App not found in this tenant — RealmAdminBootstrapper must run after AppRealmSeeder.");

            Guid CatalogId(string resource, string action)
            {
                var entry = cocoarAuthApp.Permissions
                    .FirstOrDefault(p => p.Resource == resource && p.Action == action)
                    ?? throw new InvalidOperationException(
                        $"cocoar-auth App catalog is missing permission {resource}:{action}.");
                return entry.Id;
            }

            var userManagerPermissionIds = new List<Guid>
            {
                CatalogId("user", "read"), CatalogId("user", "write"),
                CatalogId("session", "read"), CatalogId("session", "write"),
                CatalogId("authorization-group", "read"),
                CatalogId("permission-role", "read"),
                CatalogId("auth-log", "read"),
            };
            var userManagerRole = new PermissionRole
            {
                Id = Guid.NewGuid(),
                Name = "User Manager",
                Description = "Read+write users, read roles+groups+permission-roles.",
                AppId = cocoarAuthApp.Id,
                IsRealmAdmin = false,
                PermissionIds = userManagerPermissionIds,
            };
            session.Events.StartStream(userManagerRole.Id,
                new PermissionRoleCreatedEvent(
                    userManagerRole.Id, userManagerRole.Name, userManagerRole.Description,
                    userManagerRole.AppId, userManagerRole.IsRealmAdmin, userManagerRole.PermissionIds));

            var viewerPermissionIds = new List<Guid>
            {
                CatalogId("user", "read"),
                CatalogId("authorization-group", "read"),
                CatalogId("permission-role", "read"),
            };
            var viewerRole = new PermissionRole
            {
                Id = Guid.NewGuid(),
                Name = "Viewer",
                Description = "Read-only access to users, groups, roles.",
                AppId = cocoarAuthApp.Id,
                IsRealmAdmin = false,
                PermissionIds = viewerPermissionIds,
            };
            session.Events.StartStream(viewerRole.Id,
                new PermissionRoleCreatedEvent(
                    viewerRole.Id, viewerRole.Name, viewerRole.Description,
                    viewerRole.AppId, viewerRole.IsRealmAdmin, viewerRole.PermissionIds));
        }
        else
        {
            adminRoleId = existingAdminRole.Id;
        }

        // Administratoren group — add the user. If the group already exists
        // (re-bootstrap), append the user to its members instead of creating
        // a duplicate group.
        var existingGroup = await session.Query<Group>()
            .Where(g => !g.IsDeleted && g.RoleIds.Contains(adminRoleId))
            .FirstOrDefaultAsync(ct);

        if (existingGroup is null)
        {
            // PrincipalProjection (inline) builds the Group doc from
            // GroupCreatedEvent — direct Store conflicts under Marten 8.34+.
            var group = new Group
            {
                Id = Guid.NewGuid(),
                Name = "Administratoren",
                Description = "Full system access",
                MemberIds = [userId],
                RoleIds = [adminRoleId],
                BoundTo = [PermissionService.AllAppsWildcard],
            };
            session.Events.StartStream(group.Id,
                new GroupCreatedEvent(group.Id, group.Name, group.Description,
                    group.MemberIds, group.RoleIds,
                    BoundTo: group.BoundTo));
        }
        else if (!existingGroup.MemberIds.Contains(userId))
        {
            // Append the update event only; PrincipalProjection.Apply
            // mutates the existing doc. Don't re-Store the mutated record
            // — it would race the projection's own write.
            var newMemberIds = (List<Guid>)[.. existingGroup.MemberIds, userId];
            session.Events.Append(existingGroup.Id,
                new GroupUpdatedEvent(
                    existingGroup.Id, existingGroup.Name, existingGroup.Description,
                    newMemberIds, existingGroup.RoleIds,
                    Email: existingGroup.Email,
                    BoundTo: existingGroup.BoundTo));
        }
    }
}
