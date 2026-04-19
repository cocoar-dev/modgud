using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Domain.Authorization;
using Cocoar.Auth.Domain.Authorization.Events;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Services;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Cocoar.Auth.Api.Controllers;

/// <summary>
/// First-time setup controller. Only available when no admin user exists.
/// Returns 404 once an admin account has been created.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class SetupController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public SetupController(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _signInManager = signInManager;
    }

    /// <summary>
    /// Check if initial setup is required (no admin exists).
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<SetupStatusDto>> GetStatus()
    {
        var needsSetup = !await AdminExistsAsync();
        return Ok(new SetupStatusDto { NeedsSetup = needsSetup });
    }

    /// <summary>
    /// Create the first admin account. Only works when no admin exists.
    /// </summary>
    [HttpPost("create-admin")]
    public async Task<ActionResult<SetupResultDto>> CreateAdmin([FromBody] CreateAdminDto dto)
    {
        // Check if admin already exists
        if (await AdminExistsAsync())
        {
            return NotFound(new { Message = "Setup has already been completed." });
        }

        // Validate input
        if (string.IsNullOrWhiteSpace(dto.UserName))
        {
            return BadRequest(new { Message = "Username is required." });
        }

        if (string.IsNullOrWhiteSpace(dto.Password))
        {
            return BadRequest(new { Message = "Password is required." });
        }

        // Check if username is taken (edge case: non-admin user exists)
        var existingUser = await _userManager.FindByNameAsync(dto.UserName);
        if (existingUser != null)
        {
            return BadRequest(new { Message = "Username is already taken." });
        }

        // Ensure Admin role exists
        var adminRole = await _roleManager.FindByNameAsync("Admin");
        if (adminRole == null)
        {
            adminRole = new ApplicationRole("Admin", "System Administrator");
            var roleResult = await _roleManager.CreateAsync(adminRole);
            if (!roleResult.Succeeded)
            {
                return BadRequest(new
                {
                    Message = "Failed to create Admin role.",
                    Errors = roleResult.Errors.Select(e => e.Description)
                });
            }
        }

        // Create the admin user
        var user = new ApplicationUser(dto.UserName, dto.Email);
        if (!string.IsNullOrWhiteSpace(dto.FirstName))
        {
            user.SetFirstName(dto.FirstName);
        }
        if (!string.IsNullOrWhiteSpace(dto.LastName))
        {
            user.SetLastName(dto.LastName);
        }

        var createResult = await _userManager.CreateAsync(user, dto.Password);
        if (!createResult.Succeeded)
        {
            return BadRequest(new
            {
                Message = "Failed to create admin user.",
                Errors = createResult.Errors.Select(e => e.Description)
            });
        }

        // Assign admin role
        var addRoleResult = await _userManager.AddToRoleAsync(user, "Admin");
        if (!addRoleResult.Succeeded)
        {
            // Cleanup: delete the user if role assignment fails
            await _userManager.DeleteAsync(user);
            return BadRequest(new
            {
                Message = "Failed to assign Admin role.",
                Errors = addRoleResult.Errors.Select(e => e.Description)
            });
        }

        var session = HttpContext.RequestServices.GetRequiredService<IDocumentSession>();

        // ── ABAC bootstrap ──
        // Create the "System Admin" PermissionRole with full system+tenant admin scope,
        // wrap it in a "System Administrators" AuthorizationGroup, and add the new user
        // as the founding member. From here on, any further admin work routes through
        // group membership, not Identity roles.
        var systemAdminRoleId = Guid.CreateVersion7();
        session.Events.StartStream<PermissionRole>(systemAdminRoleId,
            new PermissionRoleCreatedEvent(
                Id: systemAdminRoleId,
                Name: "System Admin",
                Description: "Full system+tenant access. Granted to the bootstrap administrator.",
                ResourceType: "system",
                Permissions: ["system:admin", "tenant:admin"]));

        var systemAdminGroupId = Guid.CreateVersion7();
        session.Events.StartStream<AuthorizationGroup>(systemAdminGroupId,
            new AuthorizationGroupCreatedEvent(
                Id: systemAdminGroupId,
                Name: "System Administrators",
                Description: "Bootstrap group for the initial administrator. Holds the System Admin role.",
                MemberIds: [user.Id],
                RoleIds: [systemAdminRoleId],
                AccessScripts: []));

        await session.SaveChangesAsync();

        // Optionally load the ABAC-flavoured demo seed — extra users, permission-roles
        // and groups (including an auto-membership group and a nested-groups group)
        // so the fresh install isn't an empty admin screen.
        DemoSeedResult? demoResult = null;
        if (dto.LoadDemoData)
        {
            try
            {
                var importer = HttpContext.RequestServices.GetRequiredService<DemoSeedImportService>();
                var env = HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();
                var jsonPath = Path.Combine(env.ContentRootPath, "data", "demo-seed.json");
                demoResult = await importer.ImportAsync(jsonPath);
            }
            catch (Exception ex)
            {
                // Setup itself already succeeded; treat demo-seed failure as non-fatal
                // so the admin can still sign in and investigate.
                HttpContext.RequestServices.GetRequiredService<ILogger<SetupController>>()
                    .LogError(ex, "Demo seed import failed during setup. Admin was created; demo data was not.");
            }
        }

        // Auto-login so the user is immediately authenticated with the Admin role
        await _signInManager.SignInAsync(user, isPersistent: false);

        var message = demoResult is null
            ? "Admin account created successfully."
            : $"Admin account created. Demo seed loaded: {demoResult.Users} users, " +
              $"{demoResult.PermissionRoles} permission roles, {demoResult.AuthorizationGroups} groups " +
              $"({demoResult.AutoGroups} auto). Demo users sign in with password \"{demoResult.DefaultPassword}\".";

        return Ok(new SetupResultDto
        {
            Success = true,
            Message = message
        });
    }

    private async Task<bool> AdminExistsAsync()
    {
        var adminRole = await _roleManager.FindByNameAsync("Admin");
        if (adminRole == null)
        {
            return false;
        }

        var admins = await _userManager.GetUsersInRoleAsync("Admin");
        return admins.Count > 0;
    }
}

public record SetupStatusDto
{
    public bool NeedsSetup { get; init; }
}

public record CreateAdminDto
{
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }

    /// <summary>
    /// When true, imports <c>data/demo-seed.json</c> after creating the bootstrap
    /// admin: a handful of Persons plus PermissionRoles and AuthorizationGroups
    /// demonstrating manual membership, auto-membership with a TS predicate, and
    /// nested groups. Default false — production installs stay clean.
    /// </summary>
    public bool LoadDemoData { get; init; }
}

public record SetupResultDto
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}
