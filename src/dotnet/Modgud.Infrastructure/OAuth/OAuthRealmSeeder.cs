using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modgud.Domain.OAuth.Management;
using Modgud.Domain.OAuth.Scopes;
using DomainScopes = Modgud.Domain.OAuth.Scopes.StandardScopes;

namespace Modgud.Infrastructure.OAuth;

/// <summary>
/// Seeds the standard OpenID Connect scopes (<c>openid</c>, <c>email</c>, …)
/// and Modgud's own management-API selector into a tenant database. Called
/// from <c>RealmProvisioningService</c> on new-realm creation and from app
/// bootstrap for the system realm.
/// Idempotent — re-running has no effect once seeded.
/// <para>
/// The companion <c>LoginProviderRealmSeeder</c> in the Authentication slice
/// owns the <c>Internal</c> login provider seed.
/// </para>
/// </summary>
public static class OAuthRealmSeeder
{
    private static readonly (string Name, string DisplayName, string Description, string[] Resources)[] DefaultScopes =
    [
        (DomainScopes.OpenId, "OpenID", "Required scope for OpenID Connect", []),
        (DomainScopes.Email, "Email", "Access to email address", []),
        (DomainScopes.Profile, "Profile", "Access to profile information", []),
        (DomainScopes.Roles, "Roles", "Access to user roles per resource server", []),
        (DomainScopes.Permissions, "Permissions", "Access to your permission grants per resource server", []),
        (DomainScopes.OfflineAccess, "Offline Access", "Issue refresh tokens for offline access", []),
        (DomainScopes.Management, "Modgud Management", "Call selected Modgud management endpoints; endpoint permissions are evaluated live", [ModgudManagementApi.Audience]),
    ];

    public static async Task SeedAsync(IServiceProvider services, string tenantId, ILogger? logger = null, CancellationToken ct = default)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantId);

        var changedScopes = await SeedScopesAsync(session, ct);

        if (changedScopes > 0)
        {
            await session.SaveChangesAsync(ct);
            logger?.LogInformation(
                "Ensured OAuth defaults for tenant '{TenantId}' — {ScopeCount} scope(s) changed",
                tenantId, changedScopes);
        }
    }

    private static async Task<int> SeedScopesAsync(IDocumentSession session, CancellationToken ct)
    {
        var existing = await session.Query<OAuthScopeState>()
            .Where(s => !s.IsDeleted)
            .ToListAsync(ct);
        var existingByName = existing.ToDictionary(scope => scope.Name, StringComparer.Ordinal);

        var changed = 0;
        foreach (var (name, displayName, description, resources) in DefaultScopes)
        {
            if (existingByName.TryGetValue(name, out var current))
            {
                // This identifier was not reserved in older releases, so an
                // operator could already have created a custom scope with the
                // same name. Once it becomes a protected standard scope, its
                // security-sensitive shape must not be frozen in that legacy
                // state (wrong audience, required-on-all-clients, DCR opt-in,
                // disabled, or App-bound).
                if (name == DomainScopes.Management &&
                    await ReconcileManagementScopeAsync(
                        session, current, displayName, description, resources, ct))
                {
                    changed++;
                }

                continue;
            }

            var id = Guid.NewGuid();
            var (_, created) = OAuthScopeAggregate.Create(id, name, displayName, description, resources);
            session.Events.StartStream<OAuthScopeAggregate>(id, created);
            changed++;
        }
        return changed;
    }

    private static async Task<bool> ReconcileManagementScopeAsync(
        IDocumentSession session,
        OAuthScopeState current,
        string displayName,
        string description,
        IReadOnlyList<string> resources,
        CancellationToken ct)
    {
        var aggregate = await session.Events.AggregateStreamAsync<OAuthScopeAggregate>(
            current.Id, token: ct);
        if (aggregate is null)
        {
            // Compatibility with a very old/directly-stored scope document:
            // establish the event stream and let its inline projection replace
            // the document with the canonical system-owned shape.
            var (_, created) = OAuthScopeAggregate.Create(
                current.Id, DomainScopes.Management, displayName, description, resources);
            session.Events.StartStream<OAuthScopeAggregate>(current.Id, created);
            return true;
        }

        var changed = false;
        void Append(object @event)
        {
            session.Events.Append(current.Id, @event);
            changed = true;
        }

        if (current.DisplayName != displayName)
            Append(aggregate.SetDisplayName(displayName));
        if (current.Description != description)
            Append(aggregate.SetDescription(description));
        if (!current.Resources.SequenceEqual(resources, StringComparer.Ordinal))
            Append(aggregate.SetResources(resources));
        if (!current.Enabled)
            Append(aggregate.SetEnabled(true));
        if (current.Required)
            Append(aggregate.SetRequired(false));
        if (current.Emphasize)
            Append(aggregate.SetEmphasize(false));
        if (!current.ShowInDiscoveryDocument)
            Append(aggregate.SetShowInDiscoveryDocument(true));
        if (current.UserClaims.Count > 0)
            Append(aggregate.SetUserClaims([]));
        if (current.AppId.HasValue)
            Append(aggregate.SetAppId(null));

        if (current.Properties.ContainsKey(ScopePropertyKeys.AllowDynamicRegistrationClients))
        {
            var properties = new Dictionary<string, object?>(current.Properties);
            properties.Remove(ScopePropertyKeys.AllowDynamicRegistrationClients);
            Append(aggregate.SetProperties(properties));
        }

        return changed;
    }
}
