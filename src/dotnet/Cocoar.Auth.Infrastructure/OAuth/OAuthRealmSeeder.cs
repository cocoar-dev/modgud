using Cocoar.Auth.Domain.Identity.LoginProviders;
using Cocoar.Auth.Domain.OAuth.Scopes;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DomainScopes = Cocoar.Auth.Domain.OAuth.Scopes.StandardScopes;

namespace Cocoar.Auth.Infrastructure.OAuth;

/// <summary>
/// Seeds the standard OpenID Connect scopes (<c>openid</c>, <c>email</c>, …) and
/// the built-in <c>Internal</c> login provider into a tenant database. Called from
/// <c>RealmProvisioningService</c> on new-realm creation and from app bootstrap
/// for the system realm. Idempotent — re-running has no effect once seeded.
/// </summary>
public static class OAuthRealmSeeder
{
    private static readonly (string Name, string DisplayName, string Description)[] DefaultScopes =
    [
        (DomainScopes.OpenId, "OpenID", "Required scope for OpenID Connect"),
        (DomainScopes.Email, "Email", "Access to email address"),
        (DomainScopes.Profile, "Profile", "Access to profile information"),
        (DomainScopes.Roles, "Roles", "Access to user roles"),
        (DomainScopes.OfflineAccess, "Offline Access", "Issue refresh tokens for offline access"),
    ];

    public static async Task SeedAsync(IServiceProvider services, string tenantId, ILogger? logger = null, CancellationToken ct = default)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantId);

        var seededScopes = await SeedScopesAsync(session, ct);
        var seededProvider = await SeedInternalLoginProviderAsync(session, ct);

        if (seededScopes > 0 || seededProvider)
        {
            await session.SaveChangesAsync(ct);
            logger?.LogInformation(
                "Seeded OAuth defaults for tenant '{TenantId}' — {ScopeCount} scope(s), Internal login provider: {SeededProvider}",
                tenantId, seededScopes, seededProvider);
        }
    }

    private static async Task<int> SeedScopesAsync(IDocumentSession session, CancellationToken ct)
    {
        var existing = await session.Query<OAuthScopeState>()
            .Where(s => !s.IsDeleted)
            .Select(s => s.Name)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);

        var seeded = 0;
        foreach (var (name, displayName, description) in DefaultScopes)
        {
            if (existingSet.Contains(name)) continue;
            var id = Guid.NewGuid();
            var (_, created) = OAuthScopeAggregate.Create(id, name, displayName, description, Array.Empty<string>());
            session.Events.StartStream<OAuthScopeAggregate>(id, created);
            seeded++;
        }
        return seeded;
    }

    private static async Task<bool> SeedInternalLoginProviderAsync(IDocumentSession session, CancellationToken ct)
    {
        var existing = await session.Query<LoginProviderState>()
            .FirstOrDefaultAsync(x => x.Name == "Internal" && !x.IsDeleted, ct);
        if (existing is not null) return false;

        var id = Guid.NewGuid();
        var (_, created) = LoginProviderAggregate.Create(
            id,
            "Internal",
            "Internal Authentication",
            "Built-in password-based authentication",
            LoginProviderType.Internal,
            new Dictionary<string, string>(),
            isBuiltIn: true);
        session.Events.StartStream<LoginProviderAggregate>(id, created);
        return true;
    }
}
