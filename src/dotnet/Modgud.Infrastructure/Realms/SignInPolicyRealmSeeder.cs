using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modgud.Domain.Realms;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Infrastructure.Realms;

/// <summary>
/// ADR 0025 — a newly created realm starts with an explicit sign-in policy instead of the
/// one derived from the retired deployment settings: e-mail codes, passwords and passkeys
/// per <see cref="SignInPolicy.Defaults"/>, minimum level <c>single</c> for apps and the
/// self-service portal, and <c>multi</c> (14-day setup grace) for the realm's
/// administration — whoever administers the identity provider has a second factor.
///
/// <para>Only for new realms. Existing and adopted realms keep running under the derived
/// policy until an admin saves the section, so an upgrade changes nothing by itself.
/// Idempotent: a realm that already has a policy is left alone.</para>
/// </summary>
public static class SignInPolicyRealmSeeder
{
    public static async Task SeedNewRealmAsync(
        IServiceProvider services, string tenantId, ILogger? logger = null, CancellationToken ct = default)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantId);

        var settings = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct)
                       ?? new RealmSettingsDoc { Id = RealmSettingsDoc.SingletonId, CreatedAt = DateTimeOffset.UtcNow };
        if (settings.SignIn is not null) return;

        settings.SignIn = SignInPolicy.Defaults;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(settings);
        await session.SaveChangesAsync(ct);
        logger?.LogInformation("Seeded the default sign-in policy for new realm {Slug}", tenantId);
    }
}
