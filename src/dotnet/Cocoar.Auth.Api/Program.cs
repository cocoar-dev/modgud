using System.Security.Cryptography.X509Certificates;
using Cocoar.Auth.Api.Features;
using System.Text.Json.Serialization;
using Cocoar.Configuration.AspNetCore;
using Cocoar.Configuration.DI;
using Cocoar.Configuration.DI.Extensions;
using Cocoar.Configuration.Providers;
using Cocoar.SignalARRR.Server.ExtensionMethods;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using BuildingBlocks.EventDispatcher;
using Fido2NetLib;
using Cocoar.Auth.Infrastructure.Email;
using Cocoar.Auth.Api;
using Cocoar.Auth.Api.ExtensionMethods;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authentication.Api.Account;
using Cocoar.Auth.Authentication.Api.Account.Services;
using Cocoar.Auth.Api.Features.Admin;
using Cocoar.Auth.Api.Features.Admin.OAuth;
using Cocoar.Auth.Authentication.AuthLog;
using Cocoar.Auth.Authentication.Api.Admin;
using Cocoar.Auth.Authentication.Api.Admin.LoginProviders;
using Cocoar.Auth.Authentication.Api.ExternalAuth;
using Cocoar.Auth.Api.Features.Groups;
using Cocoar.Auth.Api.Features.Principals;
using Cocoar.Auth.Api.Features.Roles;
using Cocoar.Auth.Api.Features.Shared;
using Cocoar.Auth.Api.Features.Users;
using Cocoar.Auth.Api.Helper;
using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Infrastructure;
using Cocoar.Auth.Infrastructure.OAuth;
using Cocoar.Auth.Infrastructure.OpenIddict;
using Cocoar.Auth.Infrastructure.Persistence.DataProtection;
using Microsoft.AspNetCore.DataProtection;
using Cocoar.Auth.Api.Features.Auth.OAuth;
using Cocoar.Auth.Authentication.Setup;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;
using Cocoar.Auth.Authentication.Identity.LoginProviders;
using Cocoar.Auth.Authentication.Identity.LoginProviders.Flavors;
using Cocoar.Auth.Api.Middleware;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;
using Marten;
using Marten.Storage;
using Npgsql;
using Wolverine;


Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

Log.Information("Starting up");

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure Cocoar.Configuration (v5 builder API)
    builder.AddCocoarConfiguration(c => c
        .UseConfiguration(rule =>
        [
            // Load from configuration files (local overrides base, gitignored)
            rule.For<StartUpConfiguration>().FromFile("data/configuration.json"),
            rule.For<StartUpConfiguration>().FromFile("data/configuration.local.json"),
            rule.For<EmailConfiguration>().FromFile("data/configuration.json").Select("Email"),
            rule.For<EmailConfiguration>().FromFile("data/configuration.local.json").Select("Email"),
            rule.For<MagicLinkConfiguration>().FromFile("data/configuration.json").Select("MagicLink"),
            rule.For<MagicLinkConfiguration>().FromFile("data/configuration.local.json").Select("MagicLink"),
            rule.For<MagicLinkConfiguration>().FromEnvironment("MagicLink"),
            rule.For<EmailOtpConfiguration>().FromFile("data/configuration.json").Select("EmailOtp"),
            rule.For<EmailOtpConfiguration>().FromFile("data/configuration.local.json").Select("EmailOtp"),
            rule.For<EmailOtpConfiguration>().FromEnvironment("EmailOtp"),

            // Environment variable overrides (for CI/deployment)
            rule.For<StartUpConfiguration>().FromEnvironment(),
            rule.For<EmailConfiguration>().FromEnvironment("Email"),

            // App settings (auth feature toggles — from config file, overridable via env)
            rule.For<AppSettings>().FromFile("data/configuration.json").Select("AppSettings"),
            rule.For<AppSettings>().FromFile("data/configuration.local.json").Select("AppSettings"),
            rule.For<AppSettings>().FromEnvironment("AppSettings"),

            // OpenIddict OAuth/OIDC server settings
            rule.For<OpenIddictSettings>().FromFile("data/configuration.json").Select("OpenIddict"),
            rule.For<OpenIddictSettings>().FromFile("data/configuration.local.json").Select("OpenIddict"),
            rule.For<OpenIddictSettings>().FromEnvironment("OpenIddict"),

            // Cocoar-default Cloudflare Turnstile keys. Optional — per-realm
            // overrides win, and realms with CaptchaEnabled=false never look
            // here at all. Env-var form: `Turnstile__SiteKey` / `Turnstile__SecretKey`.
            rule.For<TurnstileSettings>().FromFile("data/configuration.json").Select("Turnstile"),
            rule.For<TurnstileSettings>().FromFile("data/configuration.local.json").Select("Turnstile"),
            rule.For<TurnstileSettings>().FromEnvironment("Turnstile"),

            // OpenTelemetry observability settings (Phase 1 foundation, see
            // website/dev-notes/future-features/observability-opentelemetry.md).
            rule.For<ObservabilitySettings>().FromFile("data/configuration.json").Select("Observability"),
            rule.For<ObservabilitySettings>().FromFile("data/configuration.local.json").Select("Observability"),
            rule.For<ObservabilitySettings>().FromEnvironment("Observability"),
        ], setup =>
        [
            setup.ConcreteType<StartUpConfiguration>().AsSingleton(),
            setup.ConcreteType<EmailConfiguration>().AsSingleton(),
            setup.ConcreteType<MagicLinkConfiguration>().AsSingleton(),
            setup.ConcreteType<EmailOtpConfiguration>().AsSingleton(),
            setup.ConcreteType<AppSettings>().AsSingleton(),
            setup.ConcreteType<OpenIddictSettings>().AsSingleton(),
            setup.ConcreteType<TurnstileSettings>().AsSingleton(),
            setup.ConcreteType<ObservabilitySettings>().AsSingleton(),
        ]));

    // Expose concrete config types as Authentication interfaces so Authentication
    // can inject them without depending on the Api project.
    builder.Services.AddSingleton<IAuthSettings>(sp => sp.GetRequiredService<AppSettings>());
    builder.Services.AddSingleton<IFeatureFlags>(sp => sp.GetRequiredService<AppSettings>().Features);
    builder.Services.AddSingleton<IServerConfiguration>(sp => sp.GetRequiredService<StartUpConfiguration>());
    builder.Services.AddSingleton<IMagicLinkConfiguration>(sp => sp.GetRequiredService<MagicLinkConfiguration>());

    var configManager = builder.GetCocoarConfigManager();
    var conf = configManager.GetRequiredConfig<StartUpConfiguration>();

    if (!string.IsNullOrWhiteSpace(conf.CertPath))
    {
        var certPath = PathHelper.GetFullPath(conf.CertPath);
        var cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, conf.CertPassword,
            X509KeyStorageFlags.DefaultKeySet, Pkcs12LoaderLimits.DangerousNoLimits);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureHttpsDefaults(httpsOptions =>
            {
                httpsOptions.ServerCertificate = cert;
            });
        });
    }

    // Trust reverse proxy headers (Sophos XG terminates HTTPS).
    //
    // PROD-03: KnownIPNetworks/KnownProxies cleared in Production means
    // X-Forwarded-Proto from anywhere is accepted, so anyone who can reach
    // Kestrel directly can spoof "HTTPS" and bypass Request.IsHttps. The
    // ProxyAllowedNetworks env var (CIDR list, comma-separated, e.g.
    // "10.0.0.0/8,192.168.1.0/24") narrows trust to the actual reverse-proxy
    // range. Empty == reject every X-Forwarded-* header in Production. In
    // Development the default behaviour stays open for ease of local work.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                                 | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                                 | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        if (builder.Environment.IsProduction())
        {
            var allowed = Environment.GetEnvironmentVariable("ProxyAllowedNetworks");
            if (!string.IsNullOrWhiteSpace(allowed))
            {
                foreach (var entry in allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Microsoft.AspNetCore.HttpOverrides.IPNetwork.TryParse(entry, out var network))
                        options.KnownNetworks.Add(network);
                }
            }
            // ForwardLimit caps the X-Forwarded-* depth — defence against a
            // chain of attacker-controlled headers being treated as trusted.
            options.ForwardLimit = 1;
        }
        else
        {
            // Dev convenience: trust loopback so localhost reverse-proxies
            // (Vite, Docker port-forwards) work without ENV setup.
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("127.0.0.0"), 8));
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.IPv6Loopback, 128));
        }
    });

    // PROD-03: HSTS defaults — 1 year max-age, includeSubDomains, preload-eligible.
    // The IETF default (30 days) is too short for an IdP that holds session
    // cookies. Operators who run on a still-warming-up domain can shorten
    // via HSTS__MaxAgeDays env override; production-public deployments should
    // keep the year so a one-time MITM doesn't downgrade the next visit.
    builder.Services.AddHsts(options =>
    {
        options.Preload = true;
        options.IncludeSubDomains = true;
        options.MaxAge = TimeSpan.FromDays(365);
    });

    builder.Services.AddProblemDetails();

    builder.Services.AddExceptionHandler(options =>
    {
        options.ExceptionHandler = async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An error occurred!",
                Detail = env.IsProduction() ? null : exception?.Message,
                Instance = context.Request.Path
            };

            context.Response.StatusCode = problemDetails.Status.Value;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(problemDetails);
        };
    });

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.SerializerOptions.PropertyNamingPolicy = null;
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.SerializerOptions.Converters.Add(new OptionalJsonConverterFactory());
        options.SerializerOptions.TypeInfoResolver = new OptionalAwareTypeInfoResolver();
    });

    builder.Services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");

    // Session (needed for Passkey registration challenge storage)
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession(options =>
    {
        options.IdleTimeout = TimeSpan.FromMinutes(5);
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // SameAsRequest: cookie carries Secure when the request itself
        // came in over HTTPS, otherwise not. With ForwardedHeaders middleware
        // configured for the reverse proxy, IsHttps reflects the public
        // scheme — production deploys always get Secure, dev over plain
        // HTTP doesn't (so the cookie is settable at all).
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.Name = "Cocoar.Auth.Session";
    });

    builder.Services.AddResponseCompression(options =>
    {
        options.ExcludedMimeTypes = new List<string>();
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        options.MimeTypes =
            ResponseCompressionDefaults.MimeTypes.Concat(
                ["application/x-javascript"]);

    });

    builder.Services.AddSignalR()
        .AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.PayloadSerializerOptions.PropertyNamingPolicy = null; // PascalCase
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

    builder.Services.AddSignalARRR(options =>
    {
        options.AddServerMethodsFrom(
            typeof(Program).Assembly
        );
    });


    builder.Services.AddSingleton<DataEventDispatcher>();

    // OpenAPI
    builder.Services.AddOpenApi();


    // FIDO2 / Passkey — domain + origins derived from PublicUrl config
    var publicUri = new Uri(conf.PublicUrl ?? conf.AppUrl ?? "https://localhost");
    var fido2Origins = new HashSet<string> { $"{publicUri.Scheme}://{publicUri.Authority}" };
    if (builder.Environment.IsDevelopment())
    {
        fido2Origins.Add("http://localhost:4300");  // Vue dev server
        fido2Origins.Add("https://localhost");
        fido2Origins.Add("https://localhost:443");
    }
    builder.Services.AddFido2(options =>
    {
        options.ServerDomain = publicUri.Host;
        options.ServerName = "Cocoar.Auth";
        options.Origins = fido2Origins;
    });

    // Identity + Cookie Authentication
    builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 8;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(1); // Short lockout to limit DoS impact
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddSignInManager<AppSignInManager>()
    .AddDefaultTokenProviders()
    .AddUserStore<EventSourcedUserStore>();

    // SESSION-01 — SecurityStampValidator. Without this, an active cookie
    // session stays valid for the cookie's full ExpireTimeSpan (30 days),
    // ignoring user-state changes (deactivation, password reset, role
    // revocation). With it, the cookie middleware re-fetches the user's
    // current security-stamp every ValidationInterval (5 min) and refuses
    // the cookie if the stamp has rolled. The standard ASP.NET Core
    // Identity helpers (UserManager.UpdatePasswordHashAsync,
    // UserManager.SetLockoutEnabledAsync, UserManager.RemoveFromRoleAsync,
    // ...) all bump the stamp internally; on user-disable we bump it
    // explicitly via UserManager.UpdateSecurityStampAsync.
    builder.Services.AddScoped<ISecurityStampValidator,
        SecurityStampValidator<ApplicationUser>>();
    builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>,
        UserClaimsPrincipalFactory<ApplicationUser>>();
    builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    {
        options.ValidationInterval = TimeSpan.FromMinutes(5);
    });

    builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
        .AddCookie(IdentityConstants.ApplicationScheme, options =>
        {
            options.Cookie.HttpOnly = true;
            // COOKIE-01: Lax (was Strict). Strict prevents the browser from sending
            // the cookie on top-level navigations from third-party origins — which
            // includes the legitimate OIDC redirect-back flow (RP redirects user to
            // /connect/authorize). With Strict the user gets re-prompted for login
            // every time an external RP starts a flow, breaking SSO. Lax still
            // blocks cross-site POSTs (the actual CSRF surface) while allowing
            // top-level GET/redirect navigations.
            options.Cookie.SameSite = SameSiteMode.Lax;
            // SameAsRequest: cookie carries Secure when the request came in
            // over HTTPS, otherwise not (Vite dev proxy serves HTTP locally).
            // ForwardedHeaders middleware ensures Request.IsHttps reflects the
            // public scheme behind the reverse proxy, so production always
            // gets Secure cookies even when Kestrel listens on plain HTTP.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.Name = "Cocoar.Auth.Auth";
            options.ExpireTimeSpan = TimeSpan.FromDays(30); // Max lifetime for persistent (RememberMe) cookies
            options.SlidingExpiration = true;
            // SESSION-01 — re-validate the user's security stamp on every
            // request (with a small per-request cache configured via
            // SecurityStampValidatorOptions.ValidationInterval). When the
            // stamp on disk no longer matches the cookie's stamp, the
            // cookie is rejected and the user must re-authenticate.
            options.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
            options.Events.OnRedirectToLogin = ctx =>
            {
                // /api/* is the SPA's data plane — surface 401 so the
                // SPA can decide where to navigate. Everything else
                // (including /connect/authorize for inbound OAuth flows
                // from third-party clients) needs a real 302 redirect
                // so the browser actually lands on the login page.
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = 401;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = 403;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
            // Login/access-denied paths — the SPA handles these client-side
            // (Vue Router routes for /login + /access-denied), but they need
            // to be valid URLs so the redirect emitted above resolves to the
            // SPA shell.
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/access-denied";
            // SPA's LoginView reads route.query.redirect, so keep the
            // parameter name aligned with the SPA convention.
            options.ReturnUrlParameter = "redirect";
        })
        // Partial sign-in cookie for 2FA flow (stores user ID between password + TOTP steps)
        .AddCookie(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.Name = "Cocoar.Auth.2FA";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5); // Short-lived — user must enter TOTP quickly
        })
        // External cookie: short-lived holder for OIDC tickets between the
        // remote authentication callback and our app-level sign-in decision.
        // SameSite=Lax so the browser keeps the cookie on the IdP→app redirect.
        // Per-scheme options get applied at runtime by DynamicOidcSchemeManager.
        .AddCookie(IdentityConstants.ExternalScheme, options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.Name = "Cocoar.Auth.External";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        })
        // Placeholder OIDC registration — wires up the OpenIdConnectHandler
        // type + options plumbing so DynamicOidcSchemeManager can add real
        // per-tenant schemes at runtime without another AddOpenIdConnect call.
        // Options for the placeholder are never consumed (no scheme traffic).
        .AddOpenIdConnect(DynamicOidcSchemeManager.SchemeNamePrefix + "placeholder", options =>
        {
            options.ClientId = "placeholder";
            options.Authority = "https://example.invalid";
            options.CallbackPath = "/_placeholder/signin-oidc";
            options.SignInScheme = IdentityConstants.ExternalScheme;
            // OIDC-02 — true in non-Development. The placeholder scheme is
            // never invoked, but keeping the safe default avoids copy-paste
            // hazards and shows up correctly under any code-search audit.
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        });
    builder.Services.AddAuthorization();

    // RATE-01 — application-level rate limiting on the auth endpoints that
    // are realistically attacker-touched on a public IdP. Infrastructure-
    // level DDoS (Sophos XG, Cloudflare) handles volumetric flooding;
    // these limits target the targeted-credential-stuffing surface where
    // each request costs us BCrypt CPU + a DB write or Postmark call.
    //
    // Policies are defence-in-depth; each endpoint that opts in carries
    // its own additional protocol-level gates (account lockout, magic-link
    // per-user rate, refresh-token reuse detection). The numbers are
    // conservative — well above any legitimate user pattern, low enough
    // to make automated attacks expensive.
    //
    // Partition keys:
    //   * /connect/token, /connect/introspect, /connect/revoke → client_id
    //     fallback to IP. OAuth credential brute-force scopes per client
    //     account so two legit clients can't starve each other.
    //   * /api/account/forgot-password, /api/account/magic-link → email
    //     (from request body) fallback to IP. Per-user rate already exists
    //     in the magic-link service; this caps the upstream invocation
    //     even before that runs.
    //   * /api/account/bootstrap-admin → IP. Bootstrap-invite consume is
    //     one-shot per token; the policy is a brake on automated probing
    //     of leaked tokens.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy("oauth-token", context =>
        {
            var key = TryReadFormField(context, "client_id")
                      ?? context.Connection.RemoteIpAddress?.ToString()
                      ?? "anon";
            return System.Threading.RateLimiting.RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: key,
                factory: _ => new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                });
        });

        options.AddPolicy("bootstrap", context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "anon";
            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ip,
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0,
                });
        });

        options.AddPolicy("password-reset", context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "anon";
            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ip,
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                });
        });

        options.AddPolicy("magic-link", context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "anon";
            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ip,
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                });
        });

        // Email-verification (re)send — same bucket sizing as magic-link.
        // Covers both authenticated 1-click and the anonymous self-service
        // form; the endpoint itself returns a generic response either way.
        options.AddPolicy("email-verification", context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "anon";
            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ip,
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                });
        });
    });

    builder.Services.AddHttpContextAccessor();

    // IPermissionService + IPrincipalEmailResolver + IPrincipalLookupService + IMembershipEvaluator
    // + IAutoMembershipRecalculator are all registered by AddCocoarAuthAuthorization
    // inside AddInfrastructure. Only keep app-specific wiring here.
    builder.Services.AddScoped<IAdminNotifier, AdminNotifier>();
    // C16: Demo-seed runs as an API client now — see scripts/seed-demo.mjs.
    // No backend service, no DI registration, no PROD-01 bracket needed:
    // the script logs in as a regular admin and POSTs through the same
    // admin API the UI uses. data/demo-seed.json stays as the input
    // contract; the JSON file is still publish-excluded (csproj) so it
    // never leaves the dev tree.

    // External auth (Phase 1–2: flavor registry + dynamic OIDC scheme registration)
    builder.Services.AddSingleton<ILoginProviderFlavor, EntraIdFlavor>();
    builder.Services.AddSingleton<ILoginProviderFlavor, GenericOidcFlavor>();
    builder.Services.AddSingleton<LoginProviderFlavorRegistry>();
    builder.Services.AddSingleton<LoginProviderSecretStore>();
    // Mirrors the LoginProvider-secret-store pattern (DataProtection,
    // host-rooted keys, dedicated Purpose so the two ciphertext blobs
    // are not interchangeable). See SelfRegistration/Captcha/CaptchaSecretStore.cs.
    builder.Services.AddSingleton<Cocoar.Auth.Authentication.SelfRegistration.Captcha.CaptchaSecretStore>();

    // Self-Registration: captcha verifier + resolver + rate-limiter +
    // orchestrator. Resolver pulls per-realm encrypted secrets via
    // CaptchaSecretStore and falls back to the Cocoar-default
    // TurnstileSettings env-var config. Each piece is independently
    // testable; orchestration lives in SelfRegistrationService.
    builder.Services.AddHttpClient(nameof(Cocoar.Auth.Authentication.SelfRegistration.Captcha.TurnstileVerifier));
    builder.Services.AddSingleton<Cocoar.Auth.Authentication.SelfRegistration.RegistrationRateLimiter>();
    builder.Services.AddSingleton<Cocoar.Auth.Authentication.SelfRegistration.Captcha.ITurnstileSecretResolver>(sp =>
    {
        var resolver = new Cocoar.Auth.Authentication.SelfRegistration.Captcha.TurnstileSecretResolver(
            sp.GetRequiredService<Cocoar.Auth.Authentication.SelfRegistration.Captcha.CaptchaSecretStore>())
        {
            SystemDefaultSecret = () => sp.GetRequiredService<TurnstileSettings>().SecretKey,
            SystemDefaultSiteKey = () => sp.GetRequiredService<TurnstileSettings>().SiteKey,
        };
        return resolver;
    });
    builder.Services.AddSingleton<Cocoar.Auth.Authentication.SelfRegistration.Captcha.TurnstileVerifier>();
    builder.Services.AddScoped<Cocoar.Auth.Authentication.SelfRegistration.ISelfRegistrationService,
        Cocoar.Auth.Authentication.SelfRegistration.SelfRegistrationService>();

    // Dynamic Client Registration — validator is stateless (scoped is fine,
    // but singleton avoids a per-request allocation). Rate limiter is
    // process-wide in-memory state so MUST be singleton.
    builder.Services.AddSingleton<Cocoar.Auth.Application.Dcr.IDcrRegistrationValidator,
        Cocoar.Auth.Application.Dcr.DcrRegistrationValidator>();
    builder.Services.AddSingleton<Cocoar.Auth.Application.Dcr.DcrRateLimiter>();

    // Tenant-scoped realm-wide settings (one singleton doc per tenant DB).
    // Owned by realm-admin via /api/admin/realm-settings; the service is
    // scoped so the injected IDocumentSession tracks the current tenant.
    builder.Services.AddScoped<Cocoar.Auth.Authentication.RealmSettings.IRealmSettingsService,
        Cocoar.Auth.Authentication.RealmSettings.RealmSettingsService>();
    builder.Services.AddScoped<Cocoar.Auth.Application.Services.ILoginProviderRealmSeeder,
        Cocoar.Auth.Authentication.Setup.LoginProviderRealmSeeder>();

    // C15 — Realm-Admin-Bootstrap (atomares User+Role+Group-Seeding). Used
    // by RecoveryCli `bootstrap-admin` and the future invite-mode endpoint.
    builder.Services.AddScoped<Cocoar.Auth.Authentication.Setup.IRealmAdminBootstrapper,
        Cocoar.Auth.Authentication.Setup.RealmAdminBootstrapper>();

    // C15b — One-shot Pending-Admin-Invite (issued by CLI without --password,
    // by RealmProvisioning's InitialAdmin path, or by the resend endpoint;
    // consumed by POST /api/account/bootstrap-admin).
    builder.Services.AddScoped<Cocoar.Auth.Authentication.Setup.IPendingAdminInviteService,
        Cocoar.Auth.Authentication.Setup.PendingAdminInviteService>();
    builder.Services.AddSingleton<UserUpdateScriptRunner>();
    builder.Services.AddSingleton<DynamicOidcSchemeManager>();
    builder.Services.AddScoped<ExternalLoginProcessor>();
    builder.Services.AddHostedService<OidcSchemeBootstrap>();

    // SETUP-01 / Setup endpoint surface eliminated in C15d. First-admin
    // onboarding goes through CP-issued bootstrap-invites (POST
    // /api/account/bootstrap-admin) or the Recovery-CLI `bootstrap-admin`
    // command. The race-window the old setup-token guarded against does
    // not exist anymore — anonymous endpoints can't elevate to admin.

    // Email (reactive — options factory reads IReactiveConfig<EmailConfiguration> on each send)
   
        IEmailService emailService;
        if (configManager.TryGetConfig<EmailConfiguration>(out var emailConf) && emailConf is not null)
        {
            emailService = emailConf.Provider switch
            {
                EmailProvider.Postmark => new PostmarkEmailService(() =>
                {
                    var c = configManager.GetRequiredConfig<EmailConfiguration>();
                    return new PostmarkEmailServiceOptions
                    {
                        ServerToken = c.Postmark.ServerToken,
                        FromAddress = c.Postmark.FromAddress,
                        FromName = c.Postmark.FromName,
                        MessageStream = c.Postmark.MessageStream,
                    };
                }),
                _ => new SmtpEmailService(() =>
                {
                    var c = configManager.GetRequiredConfig<EmailConfiguration>();
                    return new SmtpEmailServiceOptions
                    {
                        Host = c.Smtp.Host, Port = c.Smtp.Port, UseSsl = c.Smtp.UseSsl,
                        UserName = c.Smtp.UserName, Password = c.Smtp.Password,
                        FromAddress = c.Smtp.FromAddress, FromName = c.Smtp.FromName,
                    };
                }),
            };
        }
        else
        {
            // No email config — use no-op SMTP (logs warning, doesn't send)
            emailService = new SmtpEmailService(() => new SmtpEmailServiceOptions
            {
                Host = "localhost", Port = 25, FromAddress = "noreply@localhost",
            });
            Serilog.Log.Warning("No EmailConfiguration found — email sending disabled");
        }

        // Always register the configured email service (Smtp or Postmark).
        // The previous dev-only branch wrapped this in InMemoryEmailService and
        // exposed it via /api/dev/emails — but that left a Development-mode
        // surface (the dev-emails endpoint) hanging in the runtime image.
        // Test rigs that need to inspect outbound mail point Smtp at a real
        // capture server (Mailpit / smtp4dev / MailHog) instead — same SMTP
        // path that prod takes, no extra HTTP surface in the auth container.
        // InMemoryEmailService stays as a class for in-process integration
        // tests (CocoarAuthWebApplicationFactory wires it via DI override).
        builder.Services.AddSingleton<IEmailService>(emailService);
    
    builder.Services.AddScoped<IEmailOtpService, EmailOtpService>();

    // Per-user device-session tracking + GDPR self-service.
    // DeviceInfoService is a thin façade over Wangkanai.Detection (HttpContext-
    // scoped) — registered scoped so the underlying IDetectionService can be
    // resolved per request.
    // Session + GDPR services hold an IDocumentSession — scoped.
    builder.Services.AddDetection();
    builder.Services.AddScoped<Cocoar.Auth.Authentication.Sessions.IDeviceInfoService,
        Cocoar.Auth.Authentication.Sessions.DeviceInfoService>();
    builder.Services.AddScoped<Cocoar.Auth.Authentication.Sessions.ISessionService,
        Cocoar.Auth.Authentication.Sessions.SessionService>();
    builder.Services.AddScoped<Cocoar.Auth.Authentication.Gdpr.IGdprService,
        Cocoar.Auth.Authentication.Gdpr.GdprService>();

    // Infrastructure (Marten + repositories + query services + event dispatcher)
    // Authentication Marten setup (documents + events + projections) is wired via
    // UseCocoarAuthAuthentication() so Infrastructure stays unaware of Authentication.
    // OAuth admin slice (clients/scopes/APIs/login providers) is wired here too —
    // it has no separate slice project yet so the wiring lives directly in Infrastructure.
    builder.Services.AddInfrastructure(conf.DbSettings.ConnectionString,
        options =>
        {
            options.UseCocoarAuthAuthentication();
            options.UseCocoarAuthOAuth();
        });

    // OpenTelemetry foundation (Phase 1). See
    // website/dev-notes/future-features/observability-opentelemetry.md.
    var observabilitySettings = configManager.GetRequiredConfig<ObservabilitySettings>();
    builder.Services.AddCocoarAuthObservability(
        observabilitySettings,
        conf.DbSettings.ConnectionString);

    // Per-tenant DataProtection. Each realm's keys live in that realm's
    // database — a master-DB compromise yields no cookie-forgery for any
    // tenant, and a tenant-DB compromise is contained to that tenant.
    // Cookies + antiforgery survive `docker-compose down && up` as a
    // free side effect (no more login-everyone-out on deploy).
    // See HA-2a in
    // website/dev-notes/future-features/ha-multi-instance.md.
    builder.Services.AddTenantedDataProtection();

    // OpenIddict OAuth 2.0 / OIDC server — uses our custom Marten stores. Settings are
    // captured at config time so signing certs / lifetimes can be pinned before the
    // host is built. Per-realm issuer is applied at request time via RealmIssuerHandler.
    var openIddictSettings = configManager.GetRequiredConfig<OpenIddictSettings>();

    // CERT-01 / OAUTH-05: ensure signing + encryption certs exist on disk
    // before OpenIddict tries to load them. Convention: passwordless PFX
    // protected by file-system permissions (0600 on Linux), per the
    // cocoar-secrets CLI tool's recommendation. When the configured path
    // (or the default) doesn't exist on disk, auto-generate a self-signed
    // cert there — survives container restarts when the directory is
    // mounted as a volume, so tokens stay valid. Skipped in DevelopmentMode
    // since OpenIddict uses ephemeral keys then.
    if (!openIddictSettings.DevelopmentMode)
    {
        EnsureSigningCertificateExists(openIddictSettings);
        EnsureEncryptionCertificateExists(openIddictSettings);
    }

    // PROD-02 / CONFIG-01: fail closed when the runtime is Production but the
    // configuration is still in dev shape. Every check here is the kind of
    // mistake that would silently yield a public IdP in development mode (or
    // an IdP advertising a localhost issuer to remote clients) — surface it
    // at startup rather than at the first failed token validation in prod.
    if (builder.Environment.IsProduction())
    {
        if (openIddictSettings.DevelopmentMode)
            throw new InvalidOperationException(
                "OpenIddict.DevelopmentMode must be false in Production. Ephemeral " +
                "signing keys would invalidate every issued token on each restart " +
                "and disable transport security on /connect/* endpoints. Set " +
                "OpenIddict__DevelopmentMode=false.");

        if (string.IsNullOrWhiteSpace(openIddictSettings.Issuer) ||
            openIddictSettings.Issuer.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            openIddictSettings.Issuer.Contains("127.0.0.1"))
            throw new InvalidOperationException(
                $"OpenIddict.Issuer ('{openIddictSettings.Issuer}') is invalid for Production. " +
                "Set it to the public HTTPS URL of the IdP (e.g. https://auth.cocoar.dev).");

        if (openIddictSettings.Issuer.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"OpenIddict.Issuer ('{openIddictSettings.Issuer}') must use HTTPS in Production.");

        // Observability: Prometheus scrape is enabled by default and emits
        // realm-labelled internal counters — leaving it unauthenticated on a
        // public host hands an attacker free telemetry. Force a bearer token
        // in Production (the operator can still disable Prometheus entirely
        // via Observability__Prometheus__Enabled=false).
        if (observabilitySettings.Prometheus.Enabled
            && string.IsNullOrWhiteSpace(observabilitySettings.Prometheus.BearerToken))
            throw new InvalidOperationException(
                "Observability.Prometheus.Enabled is true but no BearerToken is set. " +
                "Production deployments must set Observability__Prometheus__BearerToken " +
                "to a strong random string (the scraper sends 'Authorization: Bearer …'), " +
                "or set Observability__Prometheus__Enabled=false.");
    }

    builder.Services.AddOpenIddictWithMarten(openIddictSettings);

    // Migration services for legacy Cocoar.Auth data have been removed in the
    // IdP-only baseline — no historical documents to upgrade to event streams.

    // Wolverine CQRS + Marten projection side effects.
    //
    // HA-2a — DurabilityMode is now env-overridable so a future Multi-
    // Instance Deployment can switch to Balanced without a code change.
    // Default Solo because that's correct for the supported deployment
    // shape today (one instance). Two instances in Solo mode would both
    // process the same outbox row → silent double-execution.
    var wolverineMode = Enum.TryParse<DurabilityMode>(
        Environment.GetEnvironmentVariable("Wolverine__DurabilityMode"),
        ignoreCase: true,
        out var parsedMode)
            ? parsedMode
            : DurabilityMode.Solo;

    Log.Information(
        "Wolverine running in {Mode} mode. Multi-instance deployments must " +
        "set Wolverine__DurabilityMode=Balanced (Solo is correct for the " +
        "default single-instance setup).",
        wolverineMode);

    builder.Host.UseWolverine(opts =>
    {
        opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
        opts.Discovery.IncludeAssembly(typeof(Cocoar.Auth.Authentication.Api.Admin.RecoveryCli).Assembly);
        opts.Discovery.IncludeAssembly(typeof(Cocoar.Auth.Authorization.Commands.CreateGroupCommand).Assembly);
        opts.Durability.Mode = wolverineMode;
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Auto;

        // Wolverine 6 made `ServiceLocationPolicy.NotAllowed` the default. We
        // keep that strict default — accidental new service-location dependencies
        // fail loudly at codegen — and document each known exception below.
        // Mirrors the AppBase v2.0.0 allowlist pattern; see
        // C:\git\cocoar\Cocoar.AppBase\docs\migrations\v1-to-v2.md § "Schritt 6".

        // ASP.NET Identity — UserManager<T>/SignInManager<T> take IServiceProvider
        // in their constructors by design (IPasswordHasher<T>/IUserValidator<T>
        // resolution). Not refactorable without forking Identity.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<Microsoft.AspNetCore.Identity.UserManager<Cocoar.Auth.Authentication.Domain.ApplicationUser>>();
        opts.CodeGeneration.AlwaysUseServiceLocationFor<Microsoft.AspNetCore.Identity.SignInManager<Cocoar.Auth.Authentication.Domain.ApplicationUser>>();

        // Cocoar.JsEval transitive deps — JsEngine + TsTranspiler use
        // IServiceProvider internally for module/script resolution. Temporary;
        // drops away once JsEval ships pure constructor DI (tracked as a
        // backlog item; AppBase has the same workaround).
        opts.CodeGeneration.AlwaysUseServiceLocationFor<Cocoar.Auth.Authorization.Membership.IMembershipEvaluator>();
        opts.CodeGeneration.AlwaysUseServiceLocationFor<Cocoar.Auth.Authorization.Membership.IAutoMembershipRecalculator>();

        // Auto-register Event Forwarding subscriptions for all ReferenceSyncHandler<TEvent> implementations
        ReferenceSyncRegistration.RegisterAll(opts, typeof(Program).Assembly);
    });

    // Auth log: Serilog sink → Channel → BackgroundService → Marten (7-day retention)
    var authLogSink = new AuthLogSink();
    builder.Services.AddSingleton(authLogSink);
    builder.Services.AddHostedService<AuthLogPersistenceService>();

    // DCR garbage collector — daily sweep of stale DCR clients whose
    // LastUsedAt has aged past the per-realm TTL. Soft-delete only.
    builder.Services.AddHostedService<Cocoar.Auth.Infrastructure.OpenIddict.DcrGarbageCollectorService>();

    builder.Services.AddSerilog(logConfig =>
    {
        // Global minimum: Information (so Auth: Info events are generated)
        logConfig.MinimumLevel.Information();

        // Apply namespace overrides from config
        foreach (var (key, value) in conf.Logging.LogLevel)
        {
            var k = key;
            if (value.HasValue && !k.Equals("default", StringComparison.OrdinalIgnoreCase)
                               && !k.Equals("*", StringComparison.OrdinalIgnoreCase))
            {
                logConfig.MinimumLevel.Override(k, value.Value);
            }
        }

        // Quiet noisy frameworks — only show warnings+
        logConfig.MinimumLevel.Override("Marten", Serilog.Events.LogEventLevel.Warning);
        logConfig.MinimumLevel.Override("Wolverine", Serilog.Events.LogEventLevel.Warning);
        logConfig.MinimumLevel.Override("Weasel", Serilog.Events.LogEventLevel.Warning);
        logConfig.MinimumLevel.Override("JasperFx", Serilog.Events.LogEventLevel.Warning);
        logConfig.MinimumLevel.Override("Npgsql", Serilog.Events.LogEventLevel.Warning);
        logConfig.MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning);
        logConfig.MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning);
        logConfig.MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information);

        // Auth log sink — captures ALL "Auth:" events (including Info)
        logConfig.WriteTo.Sink(authLogSink);

        // Console + File
        logConfig.WriteTo.Console(theme: AnsiConsoleTheme.Code);

        if (!string.IsNullOrWhiteSpace(conf.Logging.LogPath))
        {
            var path = PathHelper.GetFullPath(conf.Logging.LogPath);
            path = Path.Combine(path, "log.log");
            logConfig.WriteTo.File(path, rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31);
        }
    });

    var app = builder.Build();

    //if (app.Environment.IsDevelopment())
    //{
    //    app.UseDeveloperExceptionPage();
    //}

    app.UseExceptionHandler();

    app.UseResponseCompression();

    app.UseForwardedHeaders();

    // PROD-03: HSTS + HTTPS-redirect in non-Development. HSTS instructs
    // browsers to refuse HTTP for 365 days (with subdomain inclusion); the
    // redirect catches the first-ever HTTP hit before the header lands.
    // Both run AFTER UseForwardedHeaders so Request.IsHttps reflects the
    // edge protocol, not the in-cluster Kestrel-to-proxy hop.
    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    // HEADERS-01 — defence-in-depth response headers (CSP, X-Frame-Options,
    // X-Content-Type-Options, Referrer-Policy, Permissions-Policy, COOP).
    // Runs early so the headers ship even when the pipeline short-circuits
    // (auth challenge, exception handler, OpenAPI).
    app.UseMiddleware<Cocoar.Auth.Api.Middleware.SecurityHeadersMiddleware>();

    // Short-circuit attack-probe paths (.git, .env, /server-status, /wp-*, …)
    // with a clean 404 instead of falling through to the SPA fallback that
    // would otherwise return index.html with 200 for any unmatched path.
    // Closes scanner-noise findings ("/.git/config returns 200") without
    // changing real exposure surface — the SPA fallback was never leaking
    // data, but the 200 is misread by automated reports.
    app.UseMiddleware<Cocoar.Auth.Api.Middleware.WellKnownAttackPathsMiddleware>();

    // Enable OpenAPI endpoint (not in production)
    if (!app.Environment.IsProduction())
    {
        app.MapOpenApi();
    }

    app.AddLogging();


    app.UseRouting();

    // Resolve tenant from the Host header BEFORE auth runs so the
    // TenantedSessionFactory sees the correct tenant for every Marten session
    // opened during authentication / authorization (e.g. Identity user lookup).
    app.UseMiddleware<RealmMiddleware>();
    app.UseMiddleware<TenantContextMiddleware>();

    // C14 — Control-Plane / Data-Plane separation. Runs after RealmMiddleware
    // (so TenantInfo is on HttpContext) and before authentication so that
    // realm-management routes are 404-hidden from tenant hosts even before
    // the cookie is inspected.
    app.UseMiddleware<Cocoar.Auth.Api.Middleware.ControlPlaneGateMiddleware>();

    app.UseSession();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<Cocoar.Auth.Authentication.Api.Account.TwoFactorEnforcementMiddleware>();

    // CSRF defence (C6 — CSRF-02 / CSRF-03). Runs after authentication so the
    // browser's cookie has already been resolved (we want auth to be the gate
    // for who you are; CSRF middleware is the gate for "did the request come
    // from this origin"). Targets only state-changing /api/* requests; OAuth
    // endpoints (/connect/*) have their own protocol-level protections.
    app.UseMiddleware<Cocoar.Auth.Api.Middleware.CsrfDefenseMiddleware>();

    // RATE-01 — apply the rate-limit policies registered in
    // AddRateLimiter. Endpoints opt in via .RequireRateLimiting("policy")
    // (see /connect/* + /api/account/bootstrap-admin + /api/account/forgot-password +
    // /api/account/magic-link below). Endpoints without an explicit
    // policy are not rate-limited at the app layer.
    app.UseRateLimiter();

    // Observability surface: /metrics (Prometheus scrape) + /health/live +
    // /health/ready. AllowAnonymous applied inside the helper. Operator
    // must keep /metrics off the public internet — bind via reverse-proxy
    // ACL or localhost-only listener.
    app.MapCocoarAuthObservability(observabilitySettings);


    // OpenIddict OAuth/OIDC endpoints (/connect/authorize, /token, /userinfo, /logout, /consent).
    // OpenIddict's middleware is registered as part of UseOpenIddict... hooks called by
    // ASP.NET Core during AddOpenIddict. The discovery + JWKS endpoints are auto-mapped
    // by OpenIddict; only the passthrough endpoints (authorize/token/userinfo/...) need
    // explicit minimal-API handlers.
    app.MapAuthorizationEndpoints();
    app.MapConsentEndpoints();
    app.MapDcrRegistrationEndpoints();

    app.MapStatusEndpoints();
    app.MapAuthLogEndpoints("api");
    app.MapAppSettingsEndpoints("api");
    app.MapProjectionEndpoints("api");
    app.MapRealmsEndpoints("api");
    app.MapOAuthClientsEndpoints("api");
    app.MapOAuthScopesEndpoints("api");
    app.MapOAuthApisEndpoints("api");
    // Login providers (admin surface) — formerly two surfaces (the stub
    // LoginProvidersEndpoints + IdpConfigEndpoints). Phase 1 merge consolidates
    // both behind the LoginProviders endpoints living in the Authentication slice.
    app.MapAdminMagicLinkEndpoints("api");
    app.MapAdminGraceEndpoints("api");
    app.MapAdminChangeRequestEndpoints("api");
    app.MapAdminSessionEndpoints("api");
    app.MapAdminGdprEndpoints("api");
    app.MapAdminObservabilityEndpoints("api");
    app.MapAssetsEndpoints("api");
    app.MapCustomizationPagesEndpoints("api");

    // Account & Setup Endpoints (have additional strict "auth" rate limit)
    app.MapAccountEndpoints("api");
    app.MapProfileEndpoints("api");
    app.MapMfaEndpoints("api");
    app.MapEmailOtpEndpoints("api");
    app.MapPasskeyEndpoints("api");
    app.MapMagicLinkEndpoints("api");
    app.MapPasswordResetEndpoints("api");
    app.MapEmailVerificationEndpoints("api");
    app.MapRegisterEndpoints("api");
    app.MapRealmSettingsEndpoints("api");
    app.MapBootstrapEndpoints("api");
    app.MapSessionEndpoints("api");
    app.MapGdprEndpoints("api");
    app.MapExternalAuthEndpoints("api");
    app.MapProfileLinkEndpoints("api");
    app.MapLoginProvidersEndpoints("api");
    app.MapUserUpdateScriptTestEndpoint("api");

    // Marten Endpoints
    app.MapUsersEndpoints("api");
    Cocoar.Auth.Api.Features.ServiceAccounts.ServiceAccountsEndpoints.MapServiceAccountsEndpoints(app, "api");
    app.MapPrincipalEndpoints("api");
    app.MapRolesEndpoints("api");
    app.MapGroupEndpoints("api");
    Cocoar.Auth.Api.Features.Admin.Apps.AppsEndpoints.MapAppsEndpoints(app, "api");

    // /api/v1/me/* — Cookie-only, for the admin SPA's self-introspection.
    Cocoar.Auth.Api.Features.Auth.MeEndpoints.MapMeEndpoints(app, "api");

    // /api/v1/distribution/* — Bearer + RS-Auth. Server-to-server surface
    // for resource servers (TimeToDo, Knowledge, …) calling on behalf of
    // an authenticated user.
    Cocoar.Auth.Api.Features.Distribution.DistributionEndpoints.MapDistributionEndpoints(app, "api");

    // End-user VitePress documentation at /docs — auth-gated, redirect to /login on unauth.
    // MUST be BEFORE app.UseEndpoints — otherwise the SPA fallback endpoint (registered
    // inside UseSpaUI) terminates the pipeline here and swallows /docs/* requests.
    app.UseDocs();

    app.UseEndpoints(e => { });

    app.MapHARRRController<UIHub>("/signalr/ui");

    app.UseSpaUI();

    // ResourceRegistry is now instance-based and configured via AddCocoarAuthAuthorization
    // in AddInfrastructure — no static init required.

    // Enable SignalR side effects only after Wolverine is ready
    // (prevents WolverineHasNotStartedException during daemon catchup on startup)
    app.Lifetime.ApplicationStarted.Register(() =>
        Cocoar.Auth.Infrastructure.Events.ProjectionSideEffects.Enabled = true);

    // ────────────────────────────────────────────────────────────────────────
    //  Multi-tenant bootstrap (must run BEFORE app.Run() so the daemon and any
    //  hosted services see a fully provisioned master + system tenant)
    //
    //  Order matters:
    //   1. Make sure the master DB physically exists (raw SQL — Marten cannot
    //      `CREATE DATABASE` on a connection that already targets it).
    //   2. Apply Marten storage to the master DB so `realms.mt_tenant_databases`
    //      is created — required before any tenant can be registered.
    //   3. Register the "system" tenant pointing back at the master DB. This is
    //      the default tenant used when no HttpContext is available (background
    //      services, hosted services) and during single-realm dev boots.
    //   4. Apply schema again so the system tenant gets all per-tenant tables.
    //   5. Ensure the system Realm document exists in IGlobalStore.
    //   6. Warm the realm cache so middleware never blocks on first request.
    // ────────────────────────────────────────────────────────────────────────
    var mainCs = conf.DbSettings.ConnectionString;
    var bootstrapBuilder = new NpgsqlConnectionStringBuilder(mainCs);
    var baseDbName = bootstrapBuilder.Database
        ?? throw new InvalidOperationException("DbSettings.ConnectionString is missing 'Database='");
    bootstrapBuilder.Database = "postgres";

    await using (var bootstrapConn = new NpgsqlConnection(bootstrapBuilder.ConnectionString))
    {
        await bootstrapConn.OpenAsync();
        await using var checkCmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @dbName", bootstrapConn);
        checkCmd.Parameters.AddWithValue("@dbName", baseDbName);
        if (await checkCmd.ExecuteScalarAsync() is null)
        {
            var quotedName = "\"" + baseDbName.Replace("\"", "\"\"") + "\"";
            // CA2100: PostgreSQL DDL doesn't accept parameter binding for
            // object names. baseDbName originates from the operator-supplied
            // connection string (DbSettings.ConnectionString), parsed by
            // NpgsqlConnectionStringBuilder, not from any HTTP request path.
            // The quoted-identifier escaping above is defense-in-depth.
#pragma warning disable CA2100
            await using var createCmd = new NpgsqlCommand(
                $"CREATE DATABASE {quotedName}", bootstrapConn);
#pragma warning restore CA2100
            await createCmd.ExecuteNonQueryAsync();
            Log.Information("Created master database {DbName}", baseDbName);
        }
    }

    // Apply master-table tenancy schema (creates realms.mt_tenant_databases etc.)
    var store = app.Services.GetRequiredService<Marten.IDocumentStore>();
    var tenancy = (MasterTableTenancy)store.Options.Tenancy;
    await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

    // Register the "system" tenant pointing back at the master DB.
    // MasterTableTenancy has no "default tenant" concept — every session needs
    // a tenant id, so we explicitly point "system" at the master DB.
    await tenancy.AddDatabaseRecordAsync(TenantConstants.SystemTenantId, mainCs);

    // Apply schema again now that the system tenant is registered (no-op the
    // second time around for objects that already exist; populates per-tenant
    // documents/events/projections for the system DB).
    await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

    // Ensure the system Realm document exists in the global store
    using (var realmScope = app.Services.CreateScope())
    {
        var realmService = realmScope.ServiceProvider.GetRequiredService<IRealmProvisioningService>();
        await realmService.EnsureSystemRealmExistsAsync();

        // Seed default OAuth scopes + Internal login provider into the system tenant DB.
        // Idempotent — re-running on later boots is a no-op.
        await Cocoar.Auth.Infrastructure.OAuth.OAuthRealmSeeder.SeedAsync(
            realmScope.ServiceProvider,
            TenantConstants.SystemTenantId,
            realmScope.ServiceProvider.GetRequiredService<ILogger<Program>>());
        await realmScope.ServiceProvider
            .GetRequiredService<Cocoar.Auth.Application.Services.ILoginProviderRealmSeeder>()
            .SeedAsync(
                TenantConstants.SystemTenantId,
                realmScope.ServiceProvider.GetRequiredService<ILogger<Program>>());

        // Seed the system apps into the system tenant DB so app-scoped
        // permissions can resolve before the first realm creation.
        // The system realm is always the Control Plane (see
        // EnsureSystemRealmExistsAsync), so the control-plane app is
        // seeded here too. Idempotent.
        await Cocoar.Auth.Infrastructure.Authorization.AppRealmSeeder.SeedAsync(
            realmScope.ServiceProvider,
            TenantConstants.SystemTenantId,
            isControlPlane: true,
            realmScope.ServiceProvider.GetRequiredService<ILogger<Program>>());

        // Warm the realm cache (used by RealmMiddleware for fast Host → tenant resolution)
        var realmCache = realmScope.ServiceProvider.GetRequiredService<IRealmCache>();
        await realmCache.InitializeAsync();

        // Marten compiles each distinct LINQ shape lazily on first use.
        // Without a warmup the very first request that triggers a given
        // shape pays a 200-7500ms compile penalty (measured: realm
        // OrderBy(CreatedAt) was 7.5s, oauth/apis 390ms, user 220ms,
        // change-requests 140ms). The next request of the same shape
        // is then under 15ms forever.
        //
        // We touch every shape the admin SPA hits during normal navigation
        // here, against the system tenant. Marten caches LINQ→SQL per
        // DocumentStore, not per tenant — so warming with one tenant is
        // enough for all tenants. Costs: ~2-3s extra at boot, then no
        // user-visible cliff for the rest of the host's lifetime.
        try
        {
            // IGlobalStore — realm-admin queries
            await realmService.GetAllRealmsAsync();
            await realmService.GetRealmBySlugAsync(TenantConstants.SystemTenantId);

            // Tenant-scoped queries — open one IDocumentSession against the
            // system tenant and touch every read-shape the admin endpoints
            // use. Tiny ToList() against the persisted documents — even on
            // an empty tenant it's enough to compile the shape.
            using (TenantContext.Enter(TenantConstants.SystemTenantId))
            await using (var session = realmScope.ServiceProvider
                .GetRequiredService<Marten.IDocumentStore>().QuerySession(TenantConstants.SystemTenantId))
            {
                await session.Query<Cocoar.Auth.Authentication.Domain.ApplicationUser>()
                    .Where(u => !u.IsDeleted).Take(1).ToListAsync();
                // UserView is the read model the /api/user list endpoint queries —
                // distinct from ApplicationUser, separate Marten LINQ shape.
                await session.Query<Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users.UserView>()
                    .Where(u => !u.IsDeleted).OrderBy(u => u.UserName).Take(1).ToListAsync();
                // Principal polymorphism — Person + Group share a discriminator.
                // /api/account/me's permission BFS walks this projection.
                await session.Query<Cocoar.Auth.Authorization.Principals.Principal>()
                    .Where(p => !p.IsDeleted).Take(1).ToListAsync();
                await session.Query<Cocoar.Auth.Authorization.Roles.PermissionRole>()
                    .Where(r => !r.IsDeleted).Take(1).ToListAsync();
                await session.Query<Cocoar.Auth.Authorization.Principals.Group>()
                    .Where(g => !g.IsDeleted).Take(1).ToListAsync();
                await session.Query<Cocoar.Auth.Authentication.Domain.LoginProviders.LoginProvider>()
                    .Where(p => !p.IsDeleted).Take(1).ToListAsync();
                await session.Query<Cocoar.Auth.Authentication.AuthLog.AuthLogDocument>()
                    .OrderByDescending(l => l.Timestamp).Take(1).ToListAsync();
                await session.Query<Cocoar.Auth.Authentication.Domain.UserChangeRequest>()
                    .Take(1).ToListAsync();
            }

            // OAuthAdminService — separate read paths for clients/scopes/apis.
            // Each goes through OpenIddict-Marten stores which have their own
            // LINQ shapes; touching the service methods compiles them.
            var oauthAdmin = realmScope.ServiceProvider.GetRequiredService<Cocoar.Auth.Application.Services.OAuthAdminService>();
            using (TenantContext.Enter(TenantConstants.SystemTenantId))
            {
                await oauthAdmin.GetClientsAsync(new Cocoar.Auth.Application.DTOs.OAuth.PaginationRequest { PageSize = 1 });
                await oauthAdmin.GetScopesAsync();
                await oauthAdmin.GetApisAsync(new Cocoar.Auth.Application.DTOs.OAuth.PaginationRequest { PageSize = 1 });
            }
        }
        catch (Exception ex)
        {
            // Warmup is best-effort. A failure here doesn't prevent boot —
            // the first user request would just pay the cold-start cost.
            Log.Warning(ex, "Marten LINQ warmup failed (non-fatal).");
        }

        // No Control-Plane hostname validation needed: IsControlPlane is
        // computed from `Slug == RealmSlugRules.SystemSlug`, the system
        // realm is seeded once with reserved slug "system", and the
        // ControlPlaneGateMiddleware reads `tenant.IsControlPlane` (which
        // resolves to the slug check) at request time. The DB data is
        // the single source of truth — there's no ENV var to keep in
        // sync, no chicken-and-egg between operator config and seeded
        // realm Domains.
        //
        // Operators add their public hostname(s) to the system realm's
        // Domains via the Recovery CLI:
        //   recover realm-add-domain --slug system --domain auth.example.com
    }

    // Break-glass recovery CLI — run inside the container instead of starting Kestrel.
    //   dotnet Cocoar.Auth.Api.dll recover <command> [args...]
    if (args.Length > 0 && args[0].Equals("recover", StringComparison.OrdinalIgnoreCase))
    {
        return await Cocoar.Auth.Authentication.Api.Admin.RecoveryCli.RunAsync(
            app.Services, args[1..], conf, app.Environment);
    }

    app.Run(conf.AppUrl);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Tries to read a form-encoded field WITHOUT triggering a full
/// `Request.ReadFormAsync()` (which would consume the body before
/// downstream handlers see it). The rate-limiter partition logic
/// only needs to peek at <c>client_id</c> on a tiny POST body — we
/// rebuffer + parse the first chunk and seek back to the start.
/// </summary>
/// <summary>
/// Resolve <c>SigningCertificatePath</c> (or its default
/// <c>data/keys/signing.pfx</c>) and ensure the file exists. When the file
/// is missing, generate a passwordless self-signed PFX in place — survives
/// container restarts when the directory is mounted as a volume, so the
/// signing key (and therefore every issued token) stays stable.
/// </summary>
static void EnsureSigningCertificateExists(OpenIddictSettings settings)
    => EnsureCertificateExists(
        () => settings.SigningCertificatePath,
        path => settings.SigningCertificatePath = path,
        defaultRelativePath: "data/keys/signing.pfx",
        subject: "CN=Cocoar.Auth Signing",
        purpose: "signing",
        // OpenIddict's AddSigningCertificate rejects certs that don't
        // declare DigitalSignature in their X509KeyUsage extension.
        keyUsage: X509KeyUsageFlags.DigitalSignature);

/// <summary>
/// Resolve <c>EncryptionCertificatePath</c> (or its default
/// <c>data/keys/encryption.pfx</c>) and ensure the file exists. Same
/// auto-generation behaviour as the signing cert. Falls back to the
/// signing cert at use-site when the path stays unresolved (legacy
/// behaviour) — kept so an operator who deliberately leaves
/// EncryptionCertificatePath unset still gets a working server.
/// </summary>
static void EnsureEncryptionCertificateExists(OpenIddictSettings settings)
    => EnsureCertificateExists(
        () => settings.EncryptionCertificatePath,
        path => settings.EncryptionCertificatePath = path,
        defaultRelativePath: "data/keys/encryption.pfx",
        subject: "CN=Cocoar.Auth Encryption",
        purpose: "encryption",
        // OpenIddict's AddEncryptionCertificate wants a cert that can
        // wrap a content-encryption key — KeyEncipherment covers RSA-OAEP
        // wrapping which is what OpenIddict uses when token encryption
        // is enabled.
        keyUsage: X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment);

/// <summary>
/// Shared certificate-bootstrap path. Path is resolved via
/// <see cref="PathHelper.GetFullPath"/> so a relative
/// <c>data/keys/...</c> default works in both Development (relative to
/// the working directory) and the published Docker image (relative to
/// <c>/app/</c>).
/// </summary>
static void EnsureCertificateExists(
    Func<string?> getPath,
    Action<string> setPath,
    string defaultRelativePath,
    string subject,
    string purpose,
    X509KeyUsageFlags keyUsage)
{
    var configured = getPath();
    var path = string.IsNullOrWhiteSpace(configured)
        ? Cocoar.Auth.Api.Helper.PathHelper.GetFullPath(defaultRelativePath)
        : Cocoar.Auth.Api.Helper.PathHelper.GetFullPath(configured);
    setPath(path);

    if (File.Exists(path)) return;

    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    GenerateSelfSignedPfx(path, subject, keyUsage, validYears: 2, keySize: 2048);

    Log.Warning(
        "Auth: auto-generated self-signed {Purpose} certificate at {Path}. " +
        "This is fine for self-hosted Beta; replace with a managed cert " +
        "(Key Vault / Secrets Manager / cocoar-secrets generate-cert) before " +
        "going to public production.",
        purpose, path);
}

/// <summary>
/// Inline self-signed-cert generator. We don't reuse
/// <c>Cocoar.Configuration.X509Encryption.X509CertificateGenerator</c>
/// because that helper is hardcoded for content-encryption use cases
/// (KeyEncipherment + DataEncipherment) and OpenIddict's
/// <c>AddSigningCertificate</c> rejects certs without
/// <c>DigitalSignature</c> in their X509KeyUsage extension. Different
/// purposes need different KeyUsage bits, so we generate ourselves and
/// pass the flags in.
///
/// <para>Output: passwordless PFX, file-system permissions restricted
/// to owner read+write on Linux. Mirrors the cocoar-secrets CLI
/// convention.</para>
/// </summary>
static void GenerateSelfSignedPfx(
    string outputPath,
    string subject,
    X509KeyUsageFlags keyUsage,
    int validYears,
    int keySize)
{
    using var rsa = System.Security.Cryptography.RSA.Create(keySize);
    var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
        subject,
        rsa,
        System.Security.Cryptography.HashAlgorithmName.SHA256,
        System.Security.Cryptography.RSASignaturePadding.Pkcs1);

    request.CertificateExtensions.Add(
        new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: false));

    request.CertificateExtensions.Add(
        new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            keyUsage,
            critical: false));

    var notBefore = DateTimeOffset.UtcNow;
    var notAfter = notBefore.AddYears(validYears);

    using var cert = request.CreateSelfSigned(notBefore, notAfter);
    var pfxBytes = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx);
    File.WriteAllBytes(outputPath, pfxBytes);

    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(outputPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

static string? TryReadFormField(HttpContext context, string fieldName)
{
    if (!HttpMethods.IsPost(context.Request.Method)) return null;
    if (!context.Request.HasFormContentType) return null;
    try
    {
        // ASP.NET Core enables request-buffering via Form parsing's
        // own buffer; reading Form here is fine — downstream consumers
        // get the cached IFormCollection.
        var form = context.Request.ReadFormAsync().GetAwaiter().GetResult();
        return form.TryGetValue(fieldName, out var value) ? value.ToString() : null;
    }
    catch
    {
        return null;
    }
}
