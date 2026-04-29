using Marten;
using Microsoft.AspNetCore.Identity;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Events;
using Cocoar.Auth.Authentication.Sessions;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;

namespace Cocoar.Auth.Authentication.Api.Account;

public static class SetupEndpoints
{
    public record SetupStatusResponse(bool NeedsSetup, bool HasDemoSeed);
    public record CreateAdminRequest(string UserName, string Password, string? Firstname, string? Lastname, string? Email, bool LoadDemoData = false);

    public static WebApplication MapSetupEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/setup")
            .WithTags("Setup")
            .AllowAnonymous();

        group.MapGet("status", async (IDocumentSession session) =>
        {
            var needsSetup = !await AdminExistsAsync(session);
            var hasDemoSeed = File.Exists(Path.Combine("data", "demo-seed.json"));
            return Results.Ok(new SetupStatusResponse(needsSetup, hasDemoSeed));
        })
        .WithName("Setup_Status");

        group.MapPost("create-admin", async (
            CreateAdminRequest request,
            IDocumentSession session,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ISessionService sessionService,
            HttpContext context,
            IDemoSeedService? demoSeedService = null) =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Guard: only allow when no admin exists
            if (await AdminExistsAsync(session))
            {
                Serilog.Log.Warning("Auth: Setup create-admin blocked — admin already exists. IP={IP} UserName={UserName}", ip, request.UserName);
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Setup not available",
                    detail: "An administrator already exists.");
            }

            if (string.IsNullOrWhiteSpace(request.UserName))
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Validation error",
                    detail: "Username is required.");

            var normalizedUserName = request.UserName.Trim().ToLowerInvariant();

            // Create ApplicationUser with password
            var appUser = new ApplicationUser(normalizedUserName, request.Email)
            {
                Id = Guid.NewGuid(),
                Firstname = request.Firstname,
                Lastname = request.Lastname,
                IsActive = true
            };

            var createResult = await userManager.CreateAsync(appUser, request.Password);
            if (!createResult.Succeeded)
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Password error",
                    detail: string.Join("; ", createResult.Errors.Select(e => e.Description)));

            // UserCreatedEvent + UserUserNameChangedEvent + UserPasswordChangedEvent
            // are all emitted by EventSourcedUserStore.CreateAsync in a single transaction.

            // Create the Admin role (grants app:admin) and place the initial user into
            // an "Administratoren" group that carries that role. No direct user→role
            // assignments exist any more — permission-via-group is the sole path.
            var adminRole = new PermissionRole
            {
                Id = Guid.NewGuid(),
                Name = "System Admin",
                Description = "Full system access — bypasses every permission check.",
                AppSlug = AppSlugs.CocoarAuth,
                ResourceType = "app",
                Permissions = ["admin"]
            };
            session.Store(adminRole);
            session.Events.StartStream(adminRole.Id,
                new PermissionRoleCreatedEvent(adminRole.Id, adminRole.Name, adminRole.Description, adminRole.AppSlug, adminRole.ResourceType, adminRole.Permissions));

            // Seed two starter roles alongside the bypass role so an operator
            // can grant a granular role without first having to design one.
            // These roles are not assigned to anyone — admins drop them onto
            // groups via the Roles UI as needed.
            var userManagerRole = new PermissionRole
            {
                Id = Guid.NewGuid(),
                Name = "User Manager",
                Description = "Read+write users, read roles+groups+permission-roles.",
                AppSlug = AppSlugs.CocoarAuth,
                ResourceType = "app",
                Permissions =
                [
                    "user:read", "user:write",
                    "session:read", "session:write",
                    "authorization-group:read",
                    "permission-role:read",
                    "auth-log:read",
                ],
            };
            session.Store(userManagerRole);
            session.Events.StartStream(userManagerRole.Id,
                new PermissionRoleCreatedEvent(userManagerRole.Id, userManagerRole.Name, userManagerRole.Description, userManagerRole.AppSlug, userManagerRole.ResourceType, userManagerRole.Permissions));

            var viewerRole = new PermissionRole
            {
                Id = Guid.NewGuid(),
                Name = "Viewer",
                Description = "Read-only access to users, groups, roles.",
                AppSlug = AppSlugs.CocoarAuth,
                ResourceType = "app",
                Permissions =
                [
                    "user:read",
                    "authorization-group:read",
                    "permission-role:read",
                ],
            };
            session.Store(viewerRole);
            session.Events.StartStream(viewerRole.Id,
                new PermissionRoleCreatedEvent(viewerRole.Id, viewerRole.Name, viewerRole.Description, viewerRole.AppSlug, viewerRole.ResourceType, viewerRole.Permissions));

            var adminGroup = new Group
            {
                Id = Guid.NewGuid(),
                Name = "Administratoren",
                Description = "Full system access",
                MemberIds = [appUser.Id],
                RoleIds = [adminRole.Id],
                AccessScripts = [], // app:admin bypasses all access scripts
                BoundTo = [AppSlugs.CocoarAuth],
            };
            session.Store(adminGroup);
            session.Events.StartStream(adminGroup.Id,
                new GroupCreatedEvent(adminGroup.Id, adminGroup.Name, adminGroup.Description,
                    adminGroup.MemberIds, adminGroup.RoleIds, adminGroup.AccessScripts,
                    BoundTo: adminGroup.BoundTo));

            await session.SaveChangesAsync();

            // Load demo data if requested — demo-seed adds its own groups/roles and
            // joins its demo admin (AU) into the Administratoren group created above.
            object? demoResult = null;
            if (request.LoadDemoData && demoSeedService is not null)
            {
                var jsonPath = Path.Combine("data", "demo-seed.json");
                if (File.Exists(jsonPath))
                    demoResult = await demoSeedService.ImportAsync(jsonPath);
            }

            // Auto-login
            await signInManager.SignInAsync(appUser, isPersistent: false);

            await SessionTracker.RecordLoginAsync(sessionService, context, appUser.Id);

            Serilog.Log.Information("Auth: Initial admin created. User={UserName} IP={IP} DemoData={DemoData}", request.UserName, ip, request.LoadDemoData);
            return Results.Ok(new { Message = "Setup completed successfully", DemoData = demoResult });
        })
        .WithName("Setup_CreateAdmin");

        return application;
    }

    /// <summary>
    /// An admin exists iff some non-deleted group contains a role that effectively
    /// grants app:admin and at least one member. Permissions are stored as bare
    /// actions (e.g. "admin") paired with a ResourceType ("app"); PermissionService
    /// prefixes them at resolution time. We mirror that here.
    /// </summary>
    private static async Task<bool> AdminExistsAsync(IDocumentSession session)
    {
        var adminRoles = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted
                     && ((r.ResourceType == "app" && r.Permissions.Contains("admin"))
                         || r.Permissions.Contains("app:admin")))
            .ToListAsync();

        if (adminRoles.Count == 0)
            return false;

        var adminRoleIds = adminRoles.Select(r => r.Id).ToArray();

        return await session.Query<Group>()
            .Where(g => !g.IsDeleted
                     && g.MemberIds.Count > 0
                     && g.RoleIds.Any(id => id.IsOneOf(adminRoleIds)))
            .AnyAsync();
    }
}
