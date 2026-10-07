using Marten;
using Microsoft.AspNetCore.Http;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Applications;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.Realms;
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
    /// The Application a sign-in page presents, read from its return URL by the same rule
    /// that picks the sign-in's target (ADR 0025): the Host-pinned App leads; else the
    /// pending authorization's client when it is bound to exactly one App; else the App of
    /// the requested <c>resource</c>s when they all belong to one. A dynamically registered
    /// client (DCR/CIMD) is bound to no App, so for an MCP sign-in the resource decides —
    /// the page, its branding and its login options are the resource's App's, as its
    /// sign-in policy already is. <c>null</c> when nothing names a single App.
    /// </summary>
    Task<Guid?> ResolveApplicationIdForReturnUrlAsync(
        HttpContext httpContext, string? returnUrl, CancellationToken ct = default);

    /// <summary>The effective settings of <see cref="ResolveApplicationIdForReturnUrlAsync"/>'s App.</summary>
    Task<EffectiveSettings> ResolveForReturnUrlAsync(
        HttpContext httpContext, string? returnUrl, CancellationToken ct = default);

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
    IHttpContextAccessor httpContextAccessor,
    // Unused since ADR 0025 amendment 1 removed the deployment-wide sign-in level; kept so the
    // statically generated Wolverine handlers that construct this type keep compiling.
#pragma warning disable CS9113
    IAuthSettings authSettings) : IApplicationSettingsResolver
#pragma warning restore CS9113
{
    public async Task<EffectiveSettings> ResolveAsync(Guid? applicationId, CancellationToken ct = default)
    {
        var realm = await realmSettings.LoadAsync(ct);

        if (applicationId is not { } appId)
            return await WithSignInPolicyAsync(EffectiveSettings.From(realm), realm, app: null, ct);

        // An Application is in context. Its overrides doc is lazy-created on
        // first admin write, so absence is normal: a never-configured App
        // inherits every realm section and picks up the Application-default
        // facets (e.g. SelfRegPosture = JitOnOtp) via Merge.
        var app = await session.LoadAsync<ApplicationSettings>(appId, ct)
                  ?? new ApplicationSettings { Id = appId };

        return await WithSignInPolicyAsync(EffectiveSettings.Merge(realm, app), realm, app, ct);
    }

    /// <summary>ADR 0025 — a realm that never saved its sign-in policy runs under the one
    /// derived from the retired deployment settings (<see cref="SignInPolicy.FromLegacy"/>):
    /// the realm's floor, with the App's overrides layered on top and clamped to it
    /// (amendment A). Until now e-mail-code sign-in was gated by the native-grant switch, so
    /// for an App that switch (as merged for it) still decides whether it offers the code.</summary>
    private async Task<EffectiveSettings> WithSignInPolicyAsync(
        EffectiveSettings effective, Modgud.Domain.RealmSettings.RealmSettings realm, ApplicationSettings? app,
        CancellationToken ct)
    {
        if (realm.SignIn is not null) return effective;
        var floor = await SignInPolicyRules.InForceAsync(session, realm, ct);
        if (app is null) return effective with { SignIn = floor };
        var appDefaults = floor with { EmailCode = floor.EmailCode && (effective.NativeGrants?.Enabled ?? false) };
        return effective with { SignIn = EffectiveSettings.ApplySignInOverrides(floor, app.SignIn, appDefaults) };
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

    public async Task<Guid?> ResolveApplicationIdForReturnUrlAsync(
        HttpContext httpContext, string? returnUrl, CancellationToken ct = default)
    {
        if (httpContext.GetApplicationId() is { } pinned) return pinned;

        // Bounded like every other anonymous read of a return URL.
        if (returnUrl is null || returnUrl.Length > 8192 || returnUrl.IndexOfAny(['\r', '\n', '\0', '\\']) >= 0)
            return null;
        if (!Modgud.Authentication.SignIn.SignInRequirementService.TryParseAuthorize(
                returnUrl, out var clientId, out var resources))
            return null;

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            var client = await session.Query<OAuthApplicationState>()
                .FirstOrDefaultAsync(c => c.ClientId == clientId && !c.IsDeleted, ct);
            if (client is { AppIds.Count: 1 }) return client.AppIds[0];
        }

        var names = resources.Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0) return null;
        var appIds = (await session.Query<OAuthApiState>()
                .Where(a => names.Contains(a.Name) && !a.IsDeleted)
                .ToListAsync(ct))
            .Where(a => a.AppId.HasValue)
            .Select(a => a.AppId!.Value)
            .Distinct()
            .ToList();
        // Resources of several Apps: the sign-in's policy combines them, but a page can
        // only wear one App's face — the realm's, as before.
        return appIds.Count == 1 ? appIds[0] : null;
    }

    public async Task<EffectiveSettings> ResolveForReturnUrlAsync(
        HttpContext httpContext, string? returnUrl, CancellationToken ct = default) =>
        await ResolveAsync(await ResolveApplicationIdForReturnUrlAsync(httpContext, returnUrl, ct), ct);

    public Task<EffectiveSettings> ResolveForCurrentRequestAsync(CancellationToken ct = default)
    {
        var httpContext = httpContextAccessor.HttpContext;
        return httpContext is null
            ? ResolveAsync(null, ct)
            : ResolveForRequestAsync(httpContext, clientId: null, ct);
    }
}
