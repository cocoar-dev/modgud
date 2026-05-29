using Modgud.Application.Services;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Marten;
using Microsoft.Extensions.Logging;

namespace Modgud.Authentication.Setup;

/// <summary>
/// Seeds the built-in <c>Internal</c> login provider into a tenant database.
/// Called from <c>RealmProvisioningService</c> on new-realm creation and from
/// app bootstrap for the system realm. Idempotent — at most one Internal
/// provider per tenant.
/// <para>
/// Registered as <see cref="ILoginProviderRealmSeeder"/> in DI so the
/// Infrastructure-layer realm provisioning service can invoke it without
/// taking a project reference on the Authentication slice.
/// </para>
/// </summary>
public class LoginProviderRealmSeeder : ILoginProviderRealmSeeder
{
    private readonly IDocumentStore _store;

    public LoginProviderRealmSeeder(IDocumentStore store)
    {
        _store = store;
    }

    public async Task SeedAsync(string tenantId, ILogger? logger = null, CancellationToken ct = default)
    {
        await using var session = _store.LightweightSession(tenantId);

        var alreadyHasInternal = await session.Query<LoginProvider>()
            .Where(x => !x.IsDeleted && x.Type == LoginProviderType.Internal)
            .AnyAsync(ct);
        if (alreadyHasInternal) return;

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var @event = new LoginProviderAddedEvent(
            Id: id,
            Type: LoginProviderType.Internal,
            Flavor: LoginProviderFlavor.Internal,
            Slug: LoginProviderSlugRules.InternalSlug,
            DisplayName: "Internal Authentication",
            Description: "Built-in password-based authentication",
            IsBuiltIn: true,
            Enabled: true,
            ClientId: string.Empty,
            ClientSecretEncrypted: null,
            Scopes: [],
            UserUpdateScript: string.Empty,
            StoreRawClaims: false,
            RawClaimsRetentionDays: null,
            AutoCreateUsers: false,
            AllowLinking: false,
            TrustForEmailLink: false,
            TrustForAuthorization: false,
            AuthoritativeForProfile: false,
            AllowedEmailDomains: null,
            IconName: null,
            ButtonColorHex: null,
            FlavorData: null,
            CreatedAt: now);

        session.Events.StartStream<LoginProvider>(id, @event);
        await session.SaveChangesAsync(ct);

        logger?.LogInformation(
            "Seeded built-in Internal login provider for tenant '{TenantId}'",
            tenantId);
    }
}
