using Marten;
using Cocoar.Auth.Authentication.Configuration;
using Cocoar.Auth.Authorization.Roles;
using Cocoar.Auth.Authorization.Principals;

namespace Cocoar.Auth.Api.Features.Setup;

/// <summary>
/// At application start, in non-Development environments, generate the
/// first-run setup token IF (a) no admin user exists yet and (b) no token
/// file is present. The operator reads the generated token from the host
/// filesystem (or Serilog stdout) and supplies it as <c>X-Setup-Token</c>
/// on their first <c>/api/setup/create-admin</c> POST.
///
/// <para>Once an admin exists, the token file is irrelevant — the setup
/// endpoint hits the "admin already exists" gate regardless. The hosted
/// service does NOT delete a leftover token file in that case (it's not
/// our place to remove something the operator may have wanted to keep);
/// the operator can clean it up.</para>
/// </summary>
public sealed class SetupTokenBootstrap(
    IServiceScopeFactory scopeFactory,
    ISetupTokenService tokenService,
    ILogger<SetupTokenBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!tokenService.IsRequiredForCurrentEnvironment)
        {
            return;
        }

        // We only generate the token while there's no admin yet — if setup
        // has already happened on a previous boot, an attacker who reaches
        // the endpoint hits the "admin already exists" 403 anyway.
        using var scope = scopeFactory.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        if (await AnyAdminExistsAsync(session, cancellationToken))
        {
            logger.LogInformation("Setup: an administrator already exists; setup token not required.");
            return;
        }

        tokenService.TryGenerateIfMissing();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<bool> AnyAdminExistsAsync(IDocumentSession session, CancellationToken ct)
    {
        // Mirrors the AdminExistsAsync logic from the SetupEndpoints itself:
        // any non-deleted group whose role-set carries the realm:admin or
        // legacy app:admin permissions, with at least one member.
        var roles = await session.Query<PermissionRole>()
            .Where(r => !r.IsDeleted)
            .ToListAsync(ct);
        if (roles.Count == 0) return false;

        var adminRoleIds = roles
            .Where(r => r.Permissions.Any(p =>
                p.Equals("realm:admin", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(":admin", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("admin", StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Id)
            .ToHashSet();
        if (adminRoleIds.Count == 0) return false;

        var groups = await session.Query<Group>()
            .Where(g => !g.IsDeleted && g.MemberIds.Count > 0)
            .ToListAsync(ct);
        return groups.Any(g => g.RoleIds.Any(adminRoleIds.Contains));
    }
}
