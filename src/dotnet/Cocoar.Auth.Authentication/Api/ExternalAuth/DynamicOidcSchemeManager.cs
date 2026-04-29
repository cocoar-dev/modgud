using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;

namespace Cocoar.Auth.Authentication.Api.ExternalAuth;

/// <summary>
/// Adds, updates, and removes OIDC authentication schemes at runtime — one per
/// enabled <see cref="IdpConfig"/>. Scheme name is <c>Oidc_{IdpConfigId}</c>,
/// callback path <c>/signin-oidc/{IdpConfigId}</c>. A scheme is reachable by
/// the OIDC middleware as soon as <see cref="RegisterAsync"/> returns.
/// <para>
/// Each scheme's <c>OpenIdConnectOptions</c> is materialized into
/// <see cref="IOptionsMonitorCache{TOptions}"/> directly, bypassing the
/// static <c>AddOpenIdConnect(scheme, ...)</c> startup path so no DB-query
/// is needed in app-start ordering. The handler type is registered once
/// globally via a placeholder scheme in <c>Program.cs</c>.
/// </para>
/// </summary>
public class DynamicOidcSchemeManager(
    IAuthenticationSchemeProvider schemeProvider,
    IOptionsMonitorCache<OpenIdConnectOptions> oidcOptionsCache,
    IEnumerable<IPostConfigureOptions<OpenIdConnectOptions>> oidcPostConfigures,
    FlavorRegistry flavors,
    IdpSecretStore secrets,
    IHostEnvironment env,
    ILogger<DynamicOidcSchemeManager> logger)
{
    public const string SchemeNamePrefix = "Oidc_";

    public static string SchemeNameFor(Guid idpConfigId) => $"{SchemeNamePrefix}{idpConfigId:N}";

    /// <summary>
    /// Registers or updates the OIDC scheme for the given config. Safe to call
    /// multiple times — existing scheme is removed and re-added, so option
    /// changes (e.g. secret rotation) take effect on the next request without
    /// an app restart.
    /// </summary>
    public async Task RegisterAsync(IdpConfig config)
    {
        if (config.IsDeleted || !config.Enabled)
        {
            await UnregisterAsync(config.Id);
            return;
        }

        if (!flavors.TryGet(config.Flavor, out var flavor))
        {
            logger.LogWarning("Cannot register IdpConfig {Id}: unknown flavor {Flavor}", config.Id, config.Flavor);
            return;
        }

        OidcEndpoints endpoints;
        try { endpoints = flavor.DeriveEndpoints(config.FlavorData); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot register IdpConfig {Id}: flavor endpoint derivation failed", config.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ClientId))
        {
            logger.LogWarning("Cannot register IdpConfig {Id}: ClientId is empty", config.Id);
            return;
        }

        var schemeName = SchemeNameFor(config.Id);
        var callbackPath = $"/signin-oidc/{config.Id:N}";
        var signoutCallbackPath = $"/signout-callback-oidc/{config.Id:N}";
        var clientSecret = secrets.TryDecrypt(config.ClientSecretEncrypted);

        var options = new OpenIdConnectOptions
        {
            ClientId = config.ClientId,
            ClientSecret = clientSecret ?? string.Empty,
            Authority = endpoints.Authority,
            MetadataAddress = endpoints.MetadataUri,
            ResponseType = OpenIdConnectResponseType.Code,
            UsePkce = true,
            SaveTokens = false,
            GetClaimsFromUserInfoEndpoint = true,
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
        if (env.IsProduction())
        {
            options.CorrelationCookie.SameSite = SameSiteMode.None;
            options.NonceCookie.SameSite = SameSiteMode.None;
            // SecurePolicy defaults to SameAsRequest → Secure over HTTPS.
        }
        else
        {
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.None;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.None;
            options.ResponseMode = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseMode.Query;
        }

        // RequireHttpsMetadata OFF in Development (localhost IdP, test OIDC server).
        options.RequireHttpsMetadata = env.IsProduction();

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
                    "Auth: External OIDC token validated for scheme {Scheme} — sub={Subject}, iss={Issuer}",
                    ctx.Scheme.Name,
                    ctx.Principal?.FindFirst("sub")?.Value,
                    ctx.Principal?.FindFirst("iss")?.Value);
                return Task.CompletedTask;
            },
            OnRemoteFailure = ctx =>
            {
                logger.LogWarning(ctx.Failure,
                    "Auth: OIDC remote failure for scheme {Scheme}: {Error}",
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

        var scheme = new AuthenticationScheme(
            name: schemeName,
            displayName: config.DisplayName,
            handlerType: typeof(OpenIdConnectHandler));
        schemeProvider.AddScheme(scheme);

        logger.LogInformation("Auth: Registered OIDC scheme {Scheme} (IdP {Display} / {Flavor})",
            schemeName, config.DisplayName, config.Flavor);
    }

    public async Task UnregisterAsync(Guid idpConfigId)
    {
        var schemeName = SchemeNameFor(idpConfigId);
        schemeProvider.RemoveScheme(schemeName);
        oidcOptionsCache.TryRemove(schemeName);
        logger.LogInformation("Auth: Unregistered OIDC scheme {Scheme}", schemeName);
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

}
