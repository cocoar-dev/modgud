using Marten;
using Microsoft.AspNetCore.Http;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Applications;
using Modgud.Infrastructure.Persistence.Tenancy;

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

    /// <summary>
    /// Resolve the effective settings for the current request, picking the
    /// Application by the ADR-0011 signal order: the Host-pinned App (Phase 1)
    /// leads; absent that, the presented client's App when it is bound to exactly
    /// one (a client bound to zero apps is realm-wide, and one bound to several is
    /// ambiguous — both resolve to no Application override). Phase 2 guarantees a
    /// Host pin and a client App are consistent when both are present.
    /// </summary>
    Task<EffectiveSettings> ResolveForRequestAsync(
        HttpContext httpContext, string? clientId = null, CancellationToken ct = default);

    /// <summary>
    /// Host-time convenience for service-layer callers that have no
    /// <see cref="HttpContext"/> parameter: resolves against the ambient request
    /// (via <see cref="IHttpContextAccessor"/>), Application pinned by Host only.
    /// With no ambient request (CLI/background) returns the realm settings.
    /// </summary>
    Task<EffectiveSettings> ResolveForCurrentRequestAsync(CancellationToken ct = default);
}

public sealed class ApplicationSettingsResolver(
    IDocumentSession session,
    IRealmSettingsService realmSettings,
    IHttpContextAccessor httpContextAccessor) : IApplicationSettingsResolver
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

    public async Task<EffectiveSettings> ResolveForRequestAsync(
        HttpContext httpContext, string? clientId = null, CancellationToken ct = default)
    {
        // Host pin leads (Phase 1). Otherwise fall back to the client's App when
        // it is bound to exactly one — zero (realm-wide) or several (ambiguous)
        // both mean "no Application override".
        var applicationId = httpContext.GetApplicationId();
        if (applicationId is null && !string.IsNullOrEmpty(clientId))
        {
            var client = await session.Query<OAuthApplicationState>()
                .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted, ct);
            if (client is { AppIds.Count: 1 }) applicationId = client.AppIds[0];
        }

        return await ResolveAsync(applicationId, ct);
    }

    public Task<EffectiveSettings> ResolveForCurrentRequestAsync(CancellationToken ct = default)
    {
        var httpContext = httpContextAccessor.HttpContext;
        return httpContext is null
            ? ResolveAsync(null, ct)
            : ResolveForRequestAsync(httpContext, clientId: null, ct);
    }
}
