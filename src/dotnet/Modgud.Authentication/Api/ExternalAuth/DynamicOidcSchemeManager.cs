using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Api.ExternalAuth;

/// <summary>
/// Adds, updates, and removes OIDC authentication schemes at runtime — one per
/// enabled <see cref="LoginProvider"/>. Scheme name is <c>Oidc_{LoginProviderId}</c>,
/// callback path <c>/signin-oidc/{LoginProviderId}</c>. A scheme is reachable
/// by the OIDC middleware as soon as <see cref="RegisterAsync"/> returns.
/// <para>
/// Each scheme's <c>OpenIdConnectOptions</c> is materialized into
/// <see cref="IOptionsMonitorCache{TOptions}"/> directly, bypassing the
/// static <c>AddOpenIdConnect(scheme, ...)</c> startup path so no DB-query
/// is needed in app-start ordering. The handler type is registered once
/// globally via a placeholder scheme in <c>Program.cs</c>.
/// </para>
/// <para>
/// Internal-typed providers are NOT handled here — they are short-circuited
/// by the auth-flow consumers. Phase 1 leaves a soft filter (we still receive
/// them in <see cref="RegisterAsync"/> from event handlers, but the missing
/// flavor key sends them down the early-return path with a benign warning).
/// Phase 2 wires the explicit <c>Type == Oidc</c> guard in callers, plus a
/// defense-in-depth check in <see cref="RegisterAsync"/> itself; Saml/Ldap/
/// Kerberos types are also rejected here until their flavor surfaces land.
/// </para>
/// </summary>
public class DynamicOidcSchemeManager(
    IAuthenticationSchemeProvider schemeProvider,
    IOptionsMonitorCache<OpenIdConnectOptions> oidcOptionsCache,
    IEnumerable<IPostConfigureOptions<OpenIdConnectOptions>> oidcPostConfigures,
    LoginProviderFlavorRegistry flavors,
    LoginProviderSecretStore secrets,
    OidcSchemeRealmRegistry realmRegistry,
    IHostEnvironment env,
    ILogger<DynamicOidcSchemeManager> logger)
{
    public const string SchemeNamePrefix = "Oidc_";

    public static string SchemeNameFor(Guid loginProviderId) => $"{SchemeNamePrefix}{loginProviderId:N}";

    /// <summary>
    /// Registers or updates the OIDC scheme for the given provider. Safe to
    /// call multiple times — existing scheme is removed and re-added, so option
    /// changes (e.g. secret rotation) take effect on the next request without
    /// an app restart.
    /// </summary>
    public async Task RegisterAsync(LoginProvider config)
    {
        if (config.IsDeleted || !config.Enabled)
        {
            await UnregisterAsync(config.Id);
            return;
        }

        // Type-discriminator gate. Only Oidc-typed providers run through the
        // OIDC scheme machinery. Internal is short-circuited (built-in form
        // path); Saml/Ldap/Kerberos are not yet wired and skip silently with
        // an info log so the bootstrap loop and event-handler chain don't
        // raise warnings on every realm-startup.
        if (config.Type != LoginProviderType.Oidc)
        {
            logger.LogInformation(
                "skipping non-Oidc LoginProvider {Id} of type {Type}",
                config.Id, config.Type);
            return;
        }

        if (!flavors.TryGet(config.Flavor, out var flavor))
        {
            logger.LogWarning("Cannot register LoginProvider {Id}: unknown flavor {Flavor}", config.Id, config.Flavor);
            return;
        }

        OidcEndpoints endpoints;
        try { endpoints = flavor.DeriveEndpoints(config.FlavorData); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot register LoginProvider {Id}: flavor endpoint derivation failed", config.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ClientId))
        {
            logger.LogWarning("Cannot register LoginProvider {Id}: ClientId is empty", config.Id);
            return;
        }

        // Realm the cached scheme belongs to — needed because the callback path
        // is now the per-realm slug (host-blind matching, see
        // HostAwareOpenIdConnectHandler). Mirrors the SAML manager's RealmSlug
        // requirement; callers (bootstrap, event handlers) enter the realm's
        // TenantContext first.
        var realmSlug = TenantContext.CurrentOrNull
            ?? throw new InvalidOperationException(
                "DynamicOidcSchemeManager.RegisterAsync requires an ambient TenantContext " +
                "so the registered scheme knows which realm it belongs to. " +
                "Callers (bootstrap, event handlers) must enter the realm's TenantContext first.");

        var schemeName = SchemeNameFor(config.Id);
        // Callback path keyed by the admin-chosen slug (not the Guid) so the
        // redirect URI stays stable across a delete + recreate. The slug is only
        // unique per realm; HostAwareOpenIdConnectHandler disambiguates by realm.
        var callbackPath = $"/signin-oidc/{config.Slug}";
        var signoutCallbackPath = $"/signout-callback-oidc/{config.Slug}";
        var clientSecret = secrets.TryDecrypt(config.ClientSecretEncrypted);

        // Admin-configurable advanced settings (Connection → Advanced tab),
        // stored in FlavorData. Defaults match the values previously hard-coded
        // here, so providers that never touched them behave identically.
        var usePkce = ReadBool(config.FlavorData, "UsePkce", true);
        var getClaimsFromUserInfo = ReadBool(config.FlavorData, "GetClaimsFromUserInfoEndpoint", true);
        var saveTokens = ReadBool(config.FlavorData, "SaveTokens", false);
        var prompt = ReadString(config.FlavorData, "Prompt");

        var options = new OpenIdConnectOptions
        {
            ClientId = config.ClientId,
            ClientSecret = clientSecret ?? string.Empty,
            Authority = endpoints.Authority,
            MetadataAddress = endpoints.MetadataUri,
            ResponseType = OpenIdConnectResponseType.Code,
            UsePkce = usePkce,
            SaveTokens = saveTokens,
            GetClaimsFromUserInfoEndpoint = getClaimsFromUserInfo,
            CallbackPath = callbackPath,
            SignedOutCallbackPath = signoutCallbackPath,
            SignInScheme = IdentityConstants.ExternalScheme,
            TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = "name",
                RoleClaimType = "role",
            },
            // Keep claim types as the IdP issued them ("email", "groups", ...)
            // instead of the ClaimTypes.* XML-namespace URIs. The claims-transform
            // script and session-claim extraction both read by the raw names.
            MapInboundClaims = false,
        };
        foreach (var scope in config.Scopes.Count > 0 ? config.Scopes : (IEnumerable<string>)flavor.DefaultScopes)
        {
            if (!options.Scope.Contains(scope)) options.Scope.Add(scope);
        }
        options.Backchannel ??= new HttpClient();

        // Cookie + response-mode interaction, two cases:
        //
        //   Production (HTTPS):   form_post + SameSite=None; Secure
        //     Microsoft's recommendation. The callback is a cross-site POST
        //     from the IdP; SameSite=None+Secure is the only combo that lets
        //     the correlation/nonce cookies ride along. Works because HTTPS
        //     satisfies the Secure flag.
        //
        //   Dev/Test (HTTP):      query + SameSite=Lax
        //     SameSite=None requires Secure, which browsers enforce strictly
        //     on non-HTTPS — cookies would be rejected. Downgrading to Lax
        //     only works if the callback is a top-level GET (not cross-site
        //     POST), so we switch response_mode to "query" — IdP redirects
        //     with the code in the query string instead of form-posting it.
        //     Slightly more code-in-URL exposure, but fine for local dev.
        // SecurePolicy stays at the default (SameAsRequest) for both
        // CorrelationCookie and NonceCookie — that matches the public scheme
        // behind the reverse proxy in production (Secure over HTTPS) and
        // permits the cookie at all in dev over plain HTTP (no Secure flag).
        if (env.IsProduction())
        {
            options.CorrelationCookie.SameSite = SameSiteMode.None;
            options.NonceCookie.SameSite = SameSiteMode.None;
        }
        else
        {
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.ResponseMode = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseMode.Query;
        }

        // RequireHttpsMetadata OFF in Development (localhost IdP, test OIDC server).
        options.RequireHttpsMetadata = env.IsProduction();

        // Optional 'prompt' parameter — only when the admin picked one; empty
        // means "don't send prompt" (the IdP decides).
        if (!string.IsNullOrWhiteSpace(prompt))
            options.Prompt = prompt;

        // Identity.External cookie is configured in Program.cs via AddCookie
        // — we must not touch IOptionsMonitorCache<CookieAuthenticationOptions>
        // here, or we'd bypass the post-configure chain that wires
        // TicketDataFormat and SignInAsync would NRE.

        // Phase 2: on successful token validation, log and keep the ticket in
        // the External cookie so the login-flow endpoint can read it. Phase 4
        // replaces this event with user/link-matching + app cookie sign-in.
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = ctx =>
            {
                logger.LogInformation(
                    "External OIDC token validated for scheme {Scheme} — sub={Subject}, iss={Issuer}",
                    ctx.Scheme.Name,
                    ctx.Principal?.FindFirst("sub")?.Value,
                    ctx.Principal?.FindFirst("iss")?.Value);
                return Task.CompletedTask;
            },
            OnRemoteFailure = ctx =>
            {
                logger.LogWarning(ctx.Failure,
                    "OIDC remote failure for scheme {Scheme}: {Error}",
                    ctx.Scheme.Name, ctx.Failure?.Message ?? "(no detail)");
                ctx.HandleResponse();
                var detail = ctx.Failure is null ? "oidc"
                    : $"oidc:{Uri.EscapeDataString(ctx.Failure.Message ?? "unknown")}";
                ctx.Response.Redirect($"/login?error={detail}");
                return Task.CompletedTask;
            },
        };

        // Run the framework's post-configure chain so data-protection-backed
        // fields (NonceCookie, CorrelationCookie, StringDataFormat, the
        // Backchannel HttpClient, ...) get initialized — skipping this leaves
        // the OIDC handler throwing NullReferenceExceptions at challenge time.
        foreach (var post in oidcPostConfigures)
            post.PostConfigure(schemeName, options);

        // Re-register: remove first so option cache is fresh.
        schemeProvider.RemoveScheme(schemeName);
        oidcOptionsCache.TryRemove(schemeName);
        oidcOptionsCache.TryAdd(schemeName, options);

        // Record the scheme → realm mapping before adding the scheme so the
        // host-aware handler can resolve it the instant a callback arrives.
        realmRegistry.Set(schemeName, realmSlug);

        var scheme = new AuthenticationScheme(
            name: schemeName,
            displayName: config.DisplayName,
            handlerType: typeof(HostAwareOpenIdConnectHandler));
        schemeProvider.AddScheme(scheme);

        logger.LogInformation("Registered OIDC scheme {Scheme} (LoginProvider {Display} / {Flavor}) in realm {Realm}",
            schemeName, config.DisplayName, config.Flavor, realmSlug);
    }

    public async Task UnregisterAsync(Guid loginProviderId)
    {
        var schemeName = SchemeNameFor(loginProviderId);
        schemeProvider.RemoveScheme(schemeName);
        oidcOptionsCache.TryRemove(schemeName);
        realmRegistry.Remove(schemeName);
        logger.LogInformation("Unregistered OIDC scheme {Scheme}", schemeName);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Returns the currently-registered external-auth schemes for public
    /// discovery (login page buttons). Filters out the placeholder scheme.
    /// </summary>
    public async Task<IReadOnlyList<AuthenticationScheme>> GetRegisteredExternalSchemesAsync()
    {
        var all = await schemeProvider.GetAllSchemesAsync();
        return all
            .Where(s => s.Name.StartsWith(SchemeNamePrefix, StringComparison.Ordinal)
                        && s.Name != SchemeNamePrefix + "placeholder")
            .ToList();
    }

    // OIDC FlavorData stores the admin form's ConfigSchema keys verbatim
    // (PascalCase, e.g. "UsePkce") — there's no camelCase re-serialization on
    // the OIDC side — so a direct PascalCase lookup is sufficient.
    private static bool ReadBool(JsonDocument? doc, string name, bool fallback)
    {
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return fallback;
        return doc.RootElement.TryGetProperty(name, out var el)
               && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : fallback;
    }

    private static string? ReadString(JsonDocument? doc, string name)
    {
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;
        return doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }
}
