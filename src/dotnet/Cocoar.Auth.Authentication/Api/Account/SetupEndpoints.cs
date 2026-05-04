using Marten;
using Microsoft.AspNetCore.Identity;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Events;
using Cocoar.Auth.Authentication.Sessions;
using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;
using Cocoar.Auth.Authorization.Services;

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
            IWebHostEnvironment env,
            HttpContext context,
            // [FromServices] is needed because Production deliberately does NOT
            // register IDemoSeedService (PROD-01); without the attribute the
            // minimal-API binder marks the parameter "UNKNOWN" and refuses to
            // build the endpoint at startup. With the attribute it's resolved
            // optionally — null when unregistered, the seeder when registered.
            [Microsoft.AspNetCore.Mvc.FromServices] IDemoSeedService? demoSeedService = null,
            [Microsoft.AspNetCore.Mvc.FromServices] Configuration.ISetupTokenService? setupToken = null) =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // SETUP-01 — first-run-token gate. In Production (or any non-Development
            // environment) the request MUST present a matching X-Setup-Token header.
            // Closes the race-window between the IdP coming up and the legitimate
            // operator running setup: only someone with read-access to the host
            // filesystem can grab the token. Token is consumed on success.
            if (setupToken is { IsRequiredForCurrentEnvironment: true })
            {
                var presented = context.Request.Headers["X-Setup-Token"].ToString();
                if (!setupToken.ValidatePresentedToken(presented))
                {
                    Serilog.Log.Warning("Auth: Setup create-admin rejected — missing or invalid X-Setup-Token. IP={IP} UserName={UserName}", ip, request.UserName);
                    return Results.Problem(
                        statusCode: StatusCodes.Status401Unauthorized,
                        title: "Setup token required",
                        detail: $"Provide the X-Setup-Token header. The token is generated at startup and stored at {setupToken.TokenFilePath}.");
                }
            }

            // Guard: only allow when no admin exists
            if (await AdminExistsAsync(session))
            {
                Serilog.Log.Warning("Auth: Setup create-admin blocked — admin already exists. IP={IP} UserName={UserName}", ip, request.UserName);
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Setup not available",
                    detail: "An administrator already exists.");
            }

            // PROD-01 belt-and-suspenders: refuse demo-data import in Production
            // even if some misconfigured deployment shipped demo-seed.json AND
            // managed to register IDemoSeedService. The demo seed creates known-
            // password accounts (`Demo1234!`) and known-secret OAuth clients;
            // letting it run on a public deployment is a complete takeover.
            if (request.LoadDemoData && env.IsProduction())
            {
                Serilog.Log.Warning("Auth: Setup create-admin rejected LoadDemoData in Production. IP={IP} UserName={UserName}", ip, request.UserName);
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Demo data not available",
                    detail: "Demo data import is disabled in Production deployments.");
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
                // ResourceType is unused for this role — its single permission
                // is fully-qualified ("realm:admin") and passes through the
                // PermissionService expansion unchanged.
                ResourceType = "",
                Permissions = [PermissionEvaluator.RealmAdminPermission],
            };
            session.Store(adminRole);
            session.Events.StartStream(adminRole.Id,
                new PermissionRoleCreatedEvent(adminRole.Id, adminRole.Name, adminRole.Description, adminRole.AppSlug, adminRole.ResourceType, adminRole.Permissions));

            // Seed two starter roles alongside the bypass role so an operator
            // can grant a granular role without first having to design one.
            // These roles are not assigned to anyone — admins drop them onto
            // groups via the Roles UI as needed.
            // Multi-resource roles store fully-qualified permissions
            // ("cocoar-auth:user:read") in their Permissions list because a
            // single role can only have one ResourceType — bare-action
            // expansion would lock the role to one resource.
            const string a = AppSlugs.CocoarAuth;
            var userManagerRole = new PermissionRole
            {
                Id = Guid.NewGuid(),
                Name = "User Manager",
                Description = "Read+write users, read roles+groups+permission-roles.",
                AppSlug = a,
                ResourceType = "",
                Permissions =
                [
                    $"{a}:user:read", $"{a}:user:write",
                    $"{a}:session:read", $"{a}:session:write",
                    $"{a}:authorization-group:read",
                    $"{a}:permission-role:read",
                    $"{a}:auth-log:read",
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
                AppSlug = a,
                ResourceType = "",
                Permissions =
                [
                    $"{a}:user:read",
                    $"{a}:authorization-group:read",
                    $"{a}:permission-role:read",
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
                // "*" wildcard: active in every app. The system admin must be
                // able to govern any app registered in this realm.
                BoundTo = [PermissionService.AllAppsWildcard],
            };
            session.Store(adminGroup);
            session.Events.StartStream(adminGroup.Id,
                new GroupCreatedEvent(adminGroup.Id, adminGroup.Name, adminGroup.Description,
                    adminGroup.MemberIds, adminGroup.RoleIds,
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

            // SETUP-01 — consume the first-run token now that admin exists,
            // so a stolen token file from this point on cannot replay setup.
            setupToken?.ConsumeToken();

            Serilog.Log.Information("Auth: Initial admin created. User={UserName} IP={IP} DemoData={DemoData}", request.UserName, ip, request.LoadDemoData);
            return Results.Ok(new { Message = "Setup completed successfully", DemoData = demoResult });
        })
        .WithName("Setup_CreateAdmin");

        return application;
    }

    /// <summary>
    /// An admin exists iff some non-deleted group contains a role that
    /// effectively grants realm:admin (or its legacy precursor) and at least
    /// one member. Roles store the fully-qualified <c>realm:admin</c> verbatim
    /// in <see cref="PermissionRole.Permissions"/>; we still match the legacy
    /// shape (bare <c>"admin"</c> on a <c>ResourceType="app"</c> role,
    /// or fully-qualified <c>"app:admin"</c>) so a setup-status check on a
    /// freshly-restored legacy snapshot still reports correctly.
    /// </summary>
    private static async Task<bool> AdminExistsAsync(IDocumentSession session)
    {
        var adminRoles = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted
                     && (r.Permissions.Contains(PermissionEvaluator.RealmAdminPermission)
                         || (r.ResourceType == "app" && r.Permissions.Contains("admin"))
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
