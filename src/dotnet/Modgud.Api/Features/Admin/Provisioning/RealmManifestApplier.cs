using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modgud.Application.DTOs.User;
using Modgud.Application.Services;
using Modgud.Authentication.RealmSettings;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Wolverine;

namespace Modgud.Api.Features.Admin.Provisioning;

/// <summary>
/// Applies a <see cref="RealmManifest"/> in-process by calling the existing canonical
/// application operations — the engine behind declarative realm provisioning.
///
/// <para>Invariant: ZERO new write logic. Each section is dispatched to the SAME
/// operation the admin UI / admin API uses (<see cref="IRealmProvisioningService"/>,
/// <see cref="IRealmSettingsService"/>, <see cref="OAuthAdminService"/>, the user
/// Wolverine commands), so the manifest path and the manual path can never drift.</para>
///
/// <para>Tenant routing: the realm shell is created via the global store (no tenant
/// context), then the per-tenant config runs inside <c>TenantContext.Enter(slug)</c>
/// + a fresh DI scope. <c>TenantedSessionFactory</c> prefers the AsyncLocal
/// <c>TenantContext</c> over the ambient (control-plane) <c>HttpContext</c>, so the
/// writes land in the NEW realm's database even though the import is triggered from
/// the control-plane host.</para>
/// </summary>
public sealed class RealmManifestApplier
{
    private readonly IRealmProvisioningService _realms;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RealmManifestApplier> _logger;

    public RealmManifestApplier(
        IRealmProvisioningService realms,
        IServiceScopeFactory scopeFactory,
        ILogger<RealmManifestApplier> logger)
    {
        _realms = realms;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Imports a brand-new realm: the slug must NOT already exist. Provisions the realm
    /// shell (tenant DB + seed) then applies the manifest's config. If any step fails
    /// the whole partially-provisioned realm is hard-deleted, so a failed import leaves
    /// nothing behind (all-or-nothing).
    /// </summary>
    public async Task<ErrorOr<RealmImportResult>> ImportNewRealmAsync(
        RealmManifest manifest, CancellationToken ct = default)
    {
        var slug = manifest.Realm.Slug;

        if (await _realms.GetRealmBySlugAsync(slug, ct) is not null)
            return Error.Conflict("Realm.AlreadyExists",
                $"Realm '{slug}' already exists. Use UpdateRealm to modify an existing realm.");

        var realmResult = await _realms.CreateRealmAsync(manifest.Realm, ct);
        if (realmResult.IsError) return realmResult.Errors;
        var realm = realmResult.Value;

        try
        {
            var secrets = await ApplyTenantConfigAsync(slug, manifest, ct);
            _logger.LogInformation(
                "Imported realm {Slug}: {Apis} apis, {Scopes} scopes, {Clients} clients, {Users} users.",
                slug, manifest.Apis.Count, manifest.Scopes.Count, manifest.Clients.Count, manifest.Users.Count);
            return new RealmImportResult
            {
                Slug = slug,
                PrimaryDomain = realm.PrimaryDomain,
                ClientSecrets = secrets,
            };
        }
        catch (ManifestApplyException ex)
        {
            // A failed import must leave nothing behind: roll the whole realm back via
            // the prod-safe hard-delete (drops the tenant DB + the global record).
            _logger.LogError(ex,
                "Manifest apply failed for realm {Slug} ({What}); hard-deleting the partially-provisioned realm.",
                slug, ex.What);
            await _realms.HardDeleteRealmAsync(slug, ct);
            return ex.Errors;
        }
    }

    private async Task<Dictionary<string, string>> ApplyTenantConfigAsync(
        string slug, RealmManifest manifest, CancellationToken ct)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);

        // Enter the new realm's tenant context, then resolve the per-tenant services in
        // a FRESH scope so their IDocumentSession binds to this tenant (a session reads
        // TenantContext at the moment it is opened).
        using var _ = TenantContext.Enter(slug);
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        if (manifest.Settings is not null)
            EnsureOk(await sp.GetRequiredService<IRealmSettingsService>().PatchAsync(manifest.Settings, ct), "settings");

        var oauth = sp.GetRequiredService<OAuthAdminService>();

        foreach (var api in manifest.Apis)
            EnsureOk(await oauth.CreateApiAsync(api, ct), $"api '{api.Name}'");

        foreach (var scopeDto in manifest.Scopes)
            EnsureOk(await oauth.CreateScopeAsync(scopeDto, ct), $"scope '{scopeDto.Name}'");

        foreach (var client in manifest.Clients)
        {
            var created = await oauth.CreateClientAsync(client, ct);
            EnsureOk(created, $"client '{client.ClientId}'");
            if (created.Value.ClientSecret is not null)
                secrets[client.ClientId] = created.Value.ClientSecret;
        }

        // Wolverine opens the handler's Marten session from the message envelope's
        // tenant, NOT from the ambient TenantContext — so the user commands must be
        // dispatched with InvokeForTenantAsync(slug, ...) or they fall back to a
        // tenant-less session ("Default tenant does not supported").
        var bus = sp.GetRequiredService<IMessageBus>();
        foreach (var user in manifest.Users)
            EnsureOk(await bus.InvokeForTenantAsync<ErrorOr<UserDto>>(slug, user.ToCommand(), ct), $"user '{user.Email}'");

        return secrets;
    }

    private static void EnsureOk<T>(ErrorOr<T> result, string what)
    {
        if (result.IsError)
            throw new ManifestApplyException(what, result.Errors);
    }

    private sealed class ManifestApplyException(string what, List<Error> errors)
        : Exception($"Failed to apply {what}: {(errors.Count > 0 ? errors[0].Description : "unknown error")}")
    {
        public string What { get; } = what;
        public List<Error> Errors { get; } = errors;
    }
}
