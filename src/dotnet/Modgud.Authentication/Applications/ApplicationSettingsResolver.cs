using Marten;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Applications;

namespace Modgud.Authentication.Applications;

/// <summary>
/// Resolves the <see cref="EffectiveSettings"/> for a request: the tenant
/// <c>RealmSettings</c> with any Application overrides merged in (ADR-0011).
/// The injected <see cref="IDocumentSession"/> is tenant-scoped (the custom
/// <c>TenantedSessionFactory</c>), so the <see cref="ApplicationSettings"/>
/// load is automatically scoped to the current realm DB.
/// </summary>
public interface IApplicationSettingsResolver
{
    /// <summary>
    /// Resolve the effective settings. <paramref name="applicationId"/> is the
    /// in-context <c>App.Id</c> (from Phase-1 Host resolution or a
    /// client→App binding); <c>null</c> means no Application in context, which
    /// returns the realm settings unchanged (zero-behaviour path).
    /// </summary>
    Task<EffectiveSettings> ResolveAsync(Guid? applicationId, CancellationToken ct = default);
}

public sealed class ApplicationSettingsResolver(
    IDocumentSession session,
    IRealmSettingsService realmSettings) : IApplicationSettingsResolver
{
    public async Task<EffectiveSettings> ResolveAsync(Guid? applicationId, CancellationToken ct = default)
    {
        var realm = await realmSettings.LoadAsync(ct);

        if (applicationId is not { } appId)
            return EffectiveSettings.From(realm);

        // An Application is in context. Its overrides doc is lazy-created on
        // first admin write, so absence is normal: a never-configured App
        // inherits every realm section and picks up the Application-default
        // facets (e.g. SelfRegPosture = JitOnOtp) via Merge.
        var app = await session.LoadAsync<ApplicationSettings>(appId, ct)
                  ?? new ApplicationSettings { Id = appId };

        return EffectiveSettings.Merge(realm, app);
    }
}
