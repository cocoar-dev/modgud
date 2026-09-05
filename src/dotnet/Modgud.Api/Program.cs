using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Modgud.Api.Features;
using Modgud.Permissions.Abstractions;
using System.Text.Json.Serialization;
using BuildingBlocks.EventDispatcher;
using Cocoar.Configuration.AspNetCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modgud.Api.Cluster;
using Modgud.Infrastructure.Cluster;
using Modgud.Infrastructure.Scheduling;
using Cocoar.Configuration.DI;
using Cocoar.Configuration.DI.Extensions;
using Cocoar.Configuration.Providers;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using Serilog.Sinks.OpenTelemetry;
using Serilog.Sinks.SystemConsole.Themes;
using Fido2NetLib;
using Modgud.Infrastructure.Email;
using Modgud.Api;
using Modgud.Api.ExtensionMethods;
using Modgud.Authentication;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Api.Account;
using Modgud.Authentication.Api.Account.Services;
using Modgud.Api.Features.Admin;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Features.Admin.OAuth;
using Modgud.Authentication.Api.Admin;
using Modgud.Authentication.Api.Admin.LoginProviders;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Authentication.Api.ExternalAuth.Saml;
using Modgud.Api.Features.Groups;
using Modgud.Api.Features.Principals;
using Modgud.Api.Features.Roles;
using Modgud.Api.Features.Shared;
using Modgud.Api.Features.Users;
using Modgud.Api.Features.Installation;
using Modgud.Api.Helper;
using Modgud.Domain.Common;
using AuthRateLimitPolicy = Modgud.Domain.Realms.AuthRateLimitPolicy;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure;
using Modgud.Infrastructure.PositionTerminals;
using Modgud.Infrastructure.OAuth;
using Modgud.Infrastructure.OpenIddict;
using Modgud.Infrastructure.Persistence.DataProtection;
using Microsoft.AspNetCore.DataProtection;
using Modgud.Api.Features.Auth;
using Modgud.Api.Features.Auth.OAuth;
using Modgud.Authentication.Setup;
using Modgud.Authentication.Identity;
using Modgud.Authentication.Identity.ExternalAuth;
using Modgud.Authentication.Identity.LoginProviders;
using Modgud.Authentication.Identity.LoginProviders.Flavors;
using Modgud.Api.Middleware;
using Modgud.Api.Startup;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Marten;
using Marten.Storage;
using Npgsql;
using Wolverine;
using Wolverine.Marten;


Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

Log.Information("Starting up");

// Unhandled exceptions on ANY thread must end the process, explicitly. The
// runtime's own path is abort() -> SIGABRT, and when dotnet is PID 1 in a
// container without an init process the kernel never delivers that signal: the
// process sits at 100 % CPU forever, "Up" for Docker, dead for everyone else,
// and the restart policy never fires. exit() works as PID 1.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception - terminating the process");
    Log.CloseAndFlush();
    Environment.Exit(1);
};

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
            // the maintainers' 'observability-opentelemetry' design note).
            rule.For<ObservabilitySettings>().FromFile("data/configuration.json").Select("Observability"),
            rule.For<ObservabilitySettings>().FromFile("data/configuration.local.json").Select("Observability"),
            rule.For<ObservabilitySettings>().FromEnvironment("Observability"),

            // ADR 0010 — two-instance operation: drain, node name.
            // Deployment-wide by nature, hence configuration/env and not a
            // realm setting. Env form: Cluster__DrainDelaySeconds.
            rule.For<ClusterSettings>().FromFile("data/configuration.json").Select("Cluster"),
            rule.For<ClusterSettings>().FromFile("data/configuration.local.json").Select("Cluster"),
            rule.For<ClusterSettings>().FromEnvironment("Cluster"),
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
            setup.ConcreteType<ClusterSettings>().AsSingleton(),
        ]));

    // Expose concrete config types as Authentication interfaces so Authentication
    // can inject them without depending on the Api project.
    builder.Services.AddSingleton<IAuthSettings>(sp => sp.GetRequiredService<AppSettings>());
    builder.Services.AddSingleton<IFeatureFlags>(sp => sp.GetRequiredService<AppSettings>().Features);
    builder.Services.AddSingleton<IServerConfiguration>(sp => sp.GetRequiredService<StartUpConfiguration>());
    builder.Services.AddSingleton<IMagicLinkConfiguration>(sp => sp.GetRequiredService<MagicLinkConfiguration>());

    var configManager = builder.GetCocoarConfigManager();
    var conf = configManager.GetConfig<StartUpConfiguration>();

    // CONFIG-01 (cold-start ladder, Stage 0): fail loud here if required config
    // is missing — e.g. an env override that silently didn't bind (casing) and
    // left DbSettings.ConnectionString at its empty default. Without this the
    // app boots far and dies with a cryptic database error far from the cause.
    StartupValidation.ValidateRequiredConfig(conf);

    // ADR 0010 (D2) — one code path. Production always runs cluster-capable:
    // Wolverine Balanced with managed projection distribution and a clustered
    // Quartz store, whether one container is running or two. Development and
    // Testing keep the single-process shape (Marten Solo daemon, in-memory
    // Quartz) because they restart constantly and the integration suite drives
    // projections through explicit interactive daemons. There is deliberately
    // no instance-count switch: how many nodes are alive is read from
    // Wolverine's node table at runtime (IClusterNodes).
    var clusterSettings = configManager.GetConfig<ClusterSettings>();
    var clusterHosting = new ClusterHostingOptions
    {
        Coordination = builder.Environment.IsProduction()
            ? ClusterCoordination.WolverineManaged
            : ClusterCoordination.SingleProcess,
        // The behavioural integration suite owns projection progress explicitly:
        // each consistency boundary runs a fresh interactive daemon. Running the
        // production background daemon beside full database resets creates two
        // competing lifecycle owners and makes test outcomes scheduler-dependent.
        SingleProcessDaemonMode = builder.Environment.IsEnvironment("Testing") &&
                                  !builder.Configuration.GetValue<bool>("Testing:UseBackgroundProjectionDaemon")
            ? JasperFx.Events.Daemon.DaemonMode.Disabled
            : JasperFx.Events.Daemon.DaemonMode.Solo,
        NodeName = string.IsNullOrWhiteSpace(clusterSettings.NodeName)
            ? Environment.MachineName
            : clusterSettings.NodeName.Trim(),
        // ADR 0010 (D5) — Production always relays data events between nodes
        // over the master DB. Single-process hosts keep them in-process.
        CrossNodeRelay = builder.Environment.IsProduction(),
    };
    var drainDelay = builder.Environment.IsProduction() && clusterSettings.DrainDelaySeconds > 0
        ? TimeSpan.FromSeconds(clusterSettings.DrainDelaySeconds)
        : TimeSpan.Zero;

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

    // Trust reverse proxy headers (Sophos XG terminates HTTPS). PROD-03.
    //
    // The public scheme/host — and thus the per-realm OAuth issuer and every
    // outbound link — are derived from X-Forwarded-Proto/-Host, so those headers
    // must be trusted ONLY from the real proxy (ProxyAllowedNetworks CIDR list).
    // The fail-closed default and the ASP.NET Core "both known-lists empty ==
    // trust-all (NOT reject-all)" gotcha are handled in ForwardedHeadersTrust,
    // unit-tested against the real ForwardedHeadersMiddleware.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
        ForwardedHeadersTrust.Configure(
            options,
            builder.Environment.IsProduction(),
            Environment.GetEnvironmentVariable("ProxyAllowedNetworks")));

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

    // No ASP.NET session middleware: its only consumer was the passkey
    // registration challenge, which now lives in a server-side ceremony
    // document like every other WebAuthn ceremony (ADR 0010, D6). An in-memory
    // session would silently break the moment a second instance answers the
    // second request of the ceremony.

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

    builder.Services.AddSingleton<Modgud.Authentication.Sessions.IBrowserSessionConnectionRegistry,
        Modgud.Authentication.Sessions.BrowserSessionConnectionRegistry>();
    builder.Services.AddSingleton<Modgud.Api.Realtime.BrowserSessionHubFilter>();
    // ADR 0010 (D6) — a session revoked on another node has no local registry
    // entry to abort; the sweep re-validates idle connections against the DB.
    builder.Services.AddHostedService<Modgud.Api.Realtime.BrowserSessionConnectionSweeper>();

    // ADR 0010 (D5) — cross-node live updates. Every hub in Modgud is a server
    // stream fed by the in-process DataEventDispatcher observable; there are no
    // targeted sends, so a SignalR backplane would route nothing. The relay
    // makes that observable cluster-wide over the master DB (LISTEN/NOTIFY):
    // no second stateful service, nothing to configure.
    if (clusterHosting.CrossNodeRelay)
    {
        var relayNodeId = $"{clusterHosting.NodeName}-{Guid.NewGuid():N}";
        builder.Services.AddSingleton(sp => new PostgresDataEventRelay(
            conf.DbSettings.ConnectionString,
            relayNodeId,
            sp.GetRequiredService<DataEventDispatcher>,
            sp.GetRequiredService<ILogger<PostgresDataEventRelay>>()));
        builder.Services.AddSingleton<IDataEventRelay>(sp => sp.GetRequiredService<PostgresDataEventRelay>());
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PostgresDataEventRelay>());
    }
    else
    {
        builder.Services.AddSingleton<IDataEventRelay>(NoDataEventRelay.Instance);
    }

    // ADR 0010 (D7) — graceful drain: readiness 503 first, then hold the
    // shutdown so the proxy's active health check drains traffic away.
    builder.Services.AddSingleton<ShutdownState>();
    builder.Services.AddSingleton<IHostedService>(sp => new ShutdownDrainService(
        sp.GetRequiredService<IHostApplicationLifetime>(),
        sp.GetRequiredService<ShutdownState>(),
        drainDelay,
        sp.GetRequiredService<ILogger<ShutdownDrainService>>()));
    if (drainDelay > TimeSpan.Zero)
    {
        // Drain + Quartz WaitForJobsToComplete + Wolverine agent stop must fit
        // before the host gives up; Docker's stop_grace_period must cover it too.
        builder.Services.Configure<HostOptions>(o =>
            o.ShutdownTimeout = TimeSpan.FromSeconds(30) + drainDelay);
    }
    builder.Services.AddSignalR(options =>
        {
            options.AddFilter<Modgud.Api.Realtime.BrowserSessionHubFilter>();
        })
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

    builder.Services.AddScoped<Modgud.Api.Features.ChangeFeed.AppChangeFeedQueryService>();
    builder.Services.AddScoped<Modgud.Api.Features.Management.ManagementBearerAuthorizationService>();


    builder.Services.AddSingleton<DataEventDispatcher>();

    // OpenAPI
    builder.Services.AddOpenApi();


    // FIDO2 / Passkey — the relying party is PER REALM, not global. There is
    // no single ServerDomain/Origins: each WebAuthn ceremony builds an IFido2
    // whose RP ID is the CURRENT realm's PrimaryDomain (see
    // RealmScopedFido2Factory). A passkey is bound to the realm it was
    // registered on, and the same RP is resolved for both registration and
    // assertion. Scoped: one per request, keyed off the request's tenant.
    builder.Services.AddScoped<Modgud.Authentication.Identity.RealmScopedFido2Factory>();

    // ADR-0009 — resolves the effective WebAuthn RP ID for a passkey ceremony: the
    // requesting OAuth client's admin-set per-client RP ID, or the realm's
    // PrimaryDomain when unset. Shared by native login begin/redeem + native enroll
    // so the RP ID a credential is enrolled under matches what login later demands.
    builder.Services.AddScoped<Modgud.Authentication.Identity.RpIdResolver>();

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

        // SESSION-01 — preserve federated session claims across stamp revalidation.
        // On every validation pass the SecurityStampValidator REBUILDS the principal
        // from durable user state via the ClaimsPrincipalFactory, which drops the
        // session-only claims ExternalLoginProcessor stamps onto the federated
        // sign-in principal: the federation session-group (externally-derived
        // authorization, unioned into resource_access at token time) and the
        // external.* claims (logout routing, TwoFactorFederated/amr). Without
        // re-injecting them a federated session would silently lose its
        // externally-derived authorization after the first interval. Password
        // sessions carry none of these, so the loops below are a no-op for them.
        options.OnRefreshingPrincipal = context =>
        {
            var current = context.CurrentPrincipal;
            var newIdentity = context.NewPrincipal?.Identities.FirstOrDefault();
            if (current is null || newIdentity is null)
                return Task.CompletedTask;

            foreach (var claim in current.FindAll(FederationClaimTypes.SessionGroup))
                if (!newIdentity.HasClaim(claim.Type, claim.Value))
                    newIdentity.AddClaim(new Claim(claim.Type, claim.Value));

            foreach (var claim in current.Claims.Where(
                c => c.Type.StartsWith("modgud.external.", StringComparison.Ordinal)))
                if (!newIdentity.HasClaim(claim.Type, claim.Value))
                    newIdentity.AddClaim(new Claim(claim.Type, claim.Value));

            foreach (var claim in current.FindAll(
                         Modgud.Authentication.Sessions.SessionClaimTypes.BrowserSessionId))
                if (!newIdentity.HasClaim(claim.Type, claim.Value))
                    newIdentity.AddClaim(new Claim(claim.Type, claim.Value));

            return Task.CompletedTask;
        };
    });

    builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
        .AddCookie(IdentityConstants.ApplicationScheme, options =>
        {
            options.EventsType = typeof(Modgud.Authentication.Sessions.BrowserSessionCookieEvents);
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
            options.Cookie.Name = "Modgud.Auth";
            // ADR-0011 (#2) — cross-app browser SSO: widen the cookie domain to the
            // tenant's primary domain (so it spans every App subdomain under it)
            // when the request is on that domain or a child; host-only otherwise.
            options.CookieManager = new Modgud.Api.Cookies.TenantApexCookieManager();
            options.ExpireTimeSpan = TimeSpan.FromDays(30); // Max lifetime for persistent (RememberMe) cookies
            options.SlidingExpiration = true;
            // BrowserSessionCookieEvents performs authoritative session
            // validation, delegates security-stamp validation and preserves
            // the API-specific 401/403 redirect behavior.
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
            options.Cookie.Name = "Modgud.2FA";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5); // Short-lived — user must enter TOTP quickly
        })
        // "Remember this device for 2FA" cookie. We don't issue it ourselves
        // (the 2FA UI has no "remember me" checkbox today), but
        // SecurityStampValidator unconditionally signs out from this scheme
        // when it invalidates a session (e.g. after a security-stamp bump
        // from a password change or email-confirm). Without the registration
        // every Identity-cookie request that triggers stamp validation fails
        // with "No sign-out authentication handler is registered for the
        // scheme 'Identity.TwoFactorRememberMe'". Defensive-only — short
        // expiry, locked-down cookie attrs match the other Identity schemes.
        .AddCookie(IdentityConstants.TwoFactorRememberMeScheme, options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.Name = "Modgud.2FA.Remember";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
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
            options.Cookie.Name = "Modgud.External";
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
    // ADR 0007 — auth rate limiting is a subsystem (Modgud.Infrastructure.RateLimiting):
    // multi-dimensional (source / target / client / app + the silent source-registration
    // ceiling), Postgres-backed counters (correct across instances), realm + App
    // configurable, and a caller context with the capability-gated trusted-forwarder
    // header. Endpoints opt in via .RequireAuthRateLimit(policy, target: ...). The
    // ASP.NET in-process limiter is gone.
    builder.Services.AddSingleton<Modgud.Infrastructure.RateLimiting.IRateLimitConnectionSource,
        Modgud.Infrastructure.RateLimiting.MartenRateLimitConnectionSource>();
    builder.Services.AddSingleton<Modgud.Infrastructure.RateLimiting.IRateLimitStore,
        Modgud.Infrastructure.RateLimiting.PostgresRateLimitStore>();
    builder.Services.AddSingleton<Modgud.Infrastructure.RateLimiting.IRateLimitEvaluator,
        Modgud.Infrastructure.RateLimiting.RateLimitEvaluator>();
    builder.Services.AddScoped<Modgud.Authentication.RateLimiting.IAuthCallerContextFactory,
        Modgud.Authentication.RateLimiting.AuthCallerContextFactory>();
    builder.Services.AddScoped<Modgud.Authentication.RateLimiting.IRegistrationThrottle,
        Modgud.Authentication.RateLimiting.RegistrationThrottle>();
    // ADR 0008 — device-aware login throttling.
    builder.Services.AddScoped<Modgud.Authentication.Devices.IDeviceTrust,
        Modgud.Authentication.Devices.DeviceTrust>();
    builder.Services.AddScoped<Modgud.Authentication.RateLimiting.ILoginThrottle,
        Modgud.Authentication.RateLimiting.LoginThrottle>();
    builder.Services.AddScoped<Modgud.Authentication.RateLimiting.ILoginUnlockMailer,
        Modgud.Authentication.RateLimiting.LoginUnlockMailer>();
    // ADR 0009 — back-channel logout: session grants, logout-token minter, delivery client.
    builder.Services.AddScoped<Modgud.Authentication.Sessions.ISessionGrantService,
        Modgud.Authentication.Sessions.SessionGrantService>();
    builder.Services.AddSingleton<Modgud.Authentication.BackChannelLogout.LogoutTokenMinter>();
    builder.Services.AddScoped<Modgud.Authentication.BackChannelLogout.IBackChannelLogoutDeliverer,
        Modgud.Authentication.BackChannelLogout.BackChannelLogoutDeliverer>();
    // The prompt first attempt runs on this background dispatcher; the pending row in
    // the realm database is the durable record, the per-realm retry job the backstop.
    builder.Services.AddSingleton<Modgud.Authentication.BackChannelLogout.BackChannelLogoutDispatchQueue>();
    builder.Services.AddHostedService<Modgud.Authentication.BackChannelLogout.BackChannelLogoutDispatcher>();
    {
        // The SSRF guard refuses loopback/private targets at connect time. Development and
        // the test host talk to relying parties on localhost, so only they get a plain
        // handler; every other environment keeps the guard (an admin-registered URI is
        // not a reason to skip it).
        var backChannelClient = builder.Services.AddHttpClient(
            Modgud.Authentication.BackChannelLogout.BackChannelLogoutConstants.HttpClientName,
            client => client.Timeout = Modgud.Authentication.BackChannelLogout.BackChannelLogoutConstants.DeliveryTimeout);
        if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
            backChannelClient.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        else
            backChannelClient.ConfigurePrimaryHttpMessageHandler(() =>
                Modgud.Infrastructure.Http.SsrfSafeHttpHandlerFactory.Create("Back-channel logout delivery"));
    }
    // ADR 0009 — record "client holds tokens of session" on every access-token mint.
    builder.Services.AddOpenIddict().AddServer(options =>
        options.AddEventHandler(Modgud.Authentication.BackChannelLogout.SessionGrantTokenHandler.Descriptor));
    builder.Services.AddSingleton<Modgud.Application.Dcr.IDcrRateLimiter,
        Modgud.Infrastructure.RateLimiting.StoreBackedDcrRateLimiter>();

    builder.Services.AddHttpContextAccessor();

    // IPermissionService + IPrincipalEmailResolver + IPrincipalLookupService + IMembershipEvaluator
    // + IAutoMembershipRecalculator are all registered by AddModgudAuthorization
    // inside AddInfrastructure. Only keep app-specific wiring here.
    builder.Services.AddScoped<IAdminNotifier, AdminNotifier>();

    // Shared canonical App + Role create paths + admin set-password (admin endpoints +
    // provisioning applier).
    builder.Services.AddScoped<Modgud.Api.Features.Admin.Apps.AppAdminService>();
    builder.Services.AddScoped<Modgud.Api.Features.Roles.RoleAdminService>();
    builder.Services.AddScoped<Modgud.Api.Features.Users.Commands.SetUserPasswordHandler>();

    // Declarative realm provisioning — applies a RealmManifest in-process by reusing
    // the canonical admin operations (the engine behind import/apply/export).
    builder.Services.AddScoped<Modgud.Api.Features.Admin.Provisioning.RealmManifestApplier>();
    builder.Services.AddScoped<Modgud.Api.Features.Admin.Provisioning.RealmManifestExporter>();
    builder.Services.AddScoped<Modgud.Api.Features.Admin.Provisioning.RealmManifestPlanner>();
    builder.Services.AddScoped<Modgud.Api.Features.Admin.Provisioning.RealmDraftService>();

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
    builder.Services.AddSingleton<Modgud.Authentication.SelfRegistration.Captcha.CaptchaSecretStore>();

    // Self-Registration: captcha verifier + resolver + rate-limiter +
    // orchestrator. Resolver pulls per-realm encrypted secrets via
    // CaptchaSecretStore and falls back to the Cocoar-default
    // TurnstileSettings env-var config. Each piece is independently
    // testable; orchestration lives in SelfRegistrationService.
    builder.Services.AddHttpClient(nameof(Modgud.Authentication.SelfRegistration.Captcha.TurnstileVerifier));
    builder.Services.AddSingleton<Modgud.Authentication.SelfRegistration.Captcha.ITurnstileSecretResolver>(sp =>
    {
        var resolver = new Modgud.Authentication.SelfRegistration.Captcha.TurnstileSecretResolver(
            sp.GetRequiredService<Modgud.Authentication.SelfRegistration.Captcha.CaptchaSecretStore>())
        {
            SystemDefaultSecret = () => sp.GetRequiredService<TurnstileSettings>().SecretKey,
            SystemDefaultSiteKey = () => sp.GetRequiredService<TurnstileSettings>().SiteKey,
        };
        return resolver;
    });
    builder.Services.AddSingleton<Modgud.Authentication.SelfRegistration.Captcha.TurnstileVerifier>();
    builder.Services.AddScoped<Modgud.Authentication.SelfRegistration.ISelfRegistrationService,
        Modgud.Authentication.SelfRegistration.SelfRegistrationService>();

    // ADR-0012 — single-use registration invite codes (the InviteCode posture).
    // Scoped so the injected IDocumentSession tracks the current tenant DB.
    builder.Services.AddScoped<Modgud.Authentication.SelfRegistration.IRegistrationInviteService,
        Modgud.Authentication.SelfRegistration.RegistrationInviteService>();

    // Dynamic Client Registration — validator is stateless (scoped is fine,
    // but singleton avoids a per-request allocation). Rate limiter is
    // process-wide in-memory state so MUST be singleton.
    builder.Services.AddSingleton<Modgud.Application.Dcr.IDcrRegistrationValidator,
        Modgud.Application.Dcr.DcrRegistrationValidator>();

    // Tenant-scoped realm-wide settings (one singleton doc per tenant DB).
    // Owned by realm-admin via /api/admin/realm-settings; the service is
    // scoped so the injected IDocumentSession tracks the current tenant.
    builder.Services.AddScoped<Modgud.Authentication.RealmSettings.IRealmSettingsService,
        Modgud.Authentication.RealmSettings.RealmSettingsService>();

    // ADR-0011 — resolves effective settings (App overrides ⊕ RealmSettings).
    // Scoped so the injected IDocumentSession tracks the current tenant.
    builder.Services.AddScoped<Modgud.Authentication.Applications.IApplicationSettingsResolver,
        Modgud.Authentication.Applications.ApplicationSettingsResolver>();

    // ADR-0011 — native passwordless registration: creates a passwordless user
    // from an email (JIT sign-up). Scoped (uses the tenant-scoped UserManager).
    // ADR 0006 — the one registration pipeline for every public sign-up path.
    builder.Services.AddScoped<Modgud.Authentication.Registration.IRegistrationPipeline,
        Modgud.Authentication.Registration.RegistrationPipeline>();

    // ADR-0011 — resolves the product name for outbound emails (App email branding
    // ⊕ realm branding, Host-resolved). Scoped (per-request resolution).
    builder.Services.AddScoped<Modgud.Authentication.Applications.IEmailBrandingResolver,
        Modgud.Authentication.Applications.EmailBrandingResolver>();

    // ADR-0011 — admin write surface for per-Application settings overrides
    // (tenant ApplicationSettings doc + the global subdomain→App routing map).
    builder.Services.AddScoped<Modgud.Authentication.Applications.IApplicationSettingsService,
        Modgud.Authentication.Applications.ApplicationSettingsService>();

    builder.Services.AddScoped<Modgud.Application.Services.ILoginProviderRealmSeeder,
        Modgud.Authentication.Setup.LoginProviderRealmSeeder>();

    // C15 — Realm-Admin-Bootstrap (atomares User+Role+Group-Seeding). Used
    // by RecoveryCli `bootstrap-admin` and the future invite-mode endpoint.
    builder.Services.AddScoped<Modgud.Authentication.Setup.IRealmAdminBootstrapper,
        Modgud.Authentication.Setup.RealmAdminBootstrapper>();
    builder.Services.AddScoped<InstallationCompletionService>();

    // C15b — One-shot Pending-Admin-Invite (issued by CLI without --password,
    // optionally together with realm creation, or by the admin-invite endpoint;
    // consumed by POST /api/account/bootstrap-admin).
    builder.Services.AddScoped<Modgud.Authentication.Setup.IPendingAdminInviteService,
        Modgud.Authentication.Setup.PendingAdminInviteService>();

    // English-naming pass (2026-07) — one-time, idempotent boot migration that
    // renames a realm's legacy "Administratoren" admin group to "Administrators".
    // Same cold-start-walks-every-realm pattern as OidcSchemeBootstrap below.
    builder.Services.AddHostedService<Modgud.Authentication.Setup.LegacyAdminGroupRenameBootstrap>();
    // RFC 9126 (2026-07) — one-time, idempotent boot migration that backfills the
    // ept:pushed_authorization endpoint permission onto pre-existing OAuth clients
    // so they can use the advertised /connect/par endpoint (fixes unauthorized_client
    // / ID2183 without a manual re-save). Same cold-start-walks-every-realm pattern.
    builder.Services.AddHostedService<Modgud.Authentication.Setup.PushedAuthorizationPermissionBackfill>();
    builder.Services.AddSingleton<UserUpdateScriptRunner>();
    builder.Services.AddSingleton<Modgud.Authentication.Api.ExternalAuth.OidcSchemeRealmRegistry>();
    builder.Services.AddSingleton<DynamicOidcSchemeManager>();
    builder.Services.AddScoped<ExternalLoginProcessor>();

    // ADR 0010 (D6) — every node resolves the current realm's OIDC schemes and
    // SAML providers from the database on demand instead of learning about
    // them from a Wolverine handler that only ever runs on the committing node.
    // The scheme provider replacement must follow AddAuthentication above.
    builder.Services.AddSingleton<Modgud.Authentication.Api.ExternalAuth.LoginProviderSchemeMaterializer>();
    builder.Services.Replace(ServiceDescriptor.Singleton<
        Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider,
        Modgud.Authentication.Api.ExternalAuth.RealmAwareAuthenticationSchemeProvider>());
    builder.Services.AddHostedService<Modgud.Authentication.Api.ExternalAuth.LoginProviderSchemeBootstrap>();

    // SAML SP federation (cert services, login flow, scheme manager,
    // bootstrap + metadata-refresh hosted services).
    builder.Services.AddModgudSaml();

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
                    var c = configManager.GetConfig<EmailConfiguration>();
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
                    var c = configManager.GetConfig<EmailConfiguration>();
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

        // The deployment-level sender the admin preview shows when no realm/App
        // override applies — read reactively from the same config the senders use.
        builder.Services.AddSingleton<Modgud.Authentication.Applications.IEmailSenderDefaults>(
            new ConfiguredEmailSenderDefaults(
                () => configManager.TryGetConfig<EmailConfiguration>(out var c) ? c : null));
        builder.Services.AddScoped<Modgud.Authentication.Applications.IEmailPreviewService,
            Modgud.Authentication.Applications.EmailPreviewService>();

        // Always register the configured email service (Smtp or Postmark).
        // The previous dev-only branch wrapped this in InMemoryEmailService and
        // exposed it via /api/dev/emails — but that left a Development-mode
        // surface (the dev-emails endpoint) hanging in the runtime image.
        // Test rigs that need to inspect outbound mail point Smtp at a real
        // capture server (Mailpit / smtp4dev / MailHog) instead — same SMTP
        // path that prod takes, no extra HTTP surface in the auth container.
        // InMemoryEmailService stays as a class for in-process integration
        // tests (ModgudWebApplicationFactory wires it via DI override).
        builder.Services.AddSingleton<IEmailService>(emailService);
    
    builder.Services.AddScoped<IEmailOtpService, EmailOtpService>();

    // Per-user device-session tracking + GDPR self-service.
    // DeviceInfoService is a thin façade over Wangkanai.Detection (HttpContext-
    // scoped) — registered scoped so the underlying IDetectionService can be
    // resolved per request.
    // Session + GDPR services hold an IDocumentSession — scoped.
    builder.Services.AddDetection();
    builder.Services.AddScoped<Modgud.Authentication.Sessions.IDeviceInfoService,
        Modgud.Authentication.Sessions.DeviceInfoService>();
    builder.Services.AddScoped<Modgud.Authentication.Sessions.ISessionService,
        Modgud.Authentication.Sessions.SessionService>();
    builder.Services.AddScoped<Modgud.Authentication.Sessions.BrowserSessionCookieEvents>();
    builder.Services.AddScoped<Modgud.Authentication.Sessions.ClientSessionService>();
    builder.Services.AddScoped<Modgud.Authentication.Sessions.IClientSessionService>(sp =>
        sp.GetRequiredService<Modgud.Authentication.Sessions.ClientSessionService>());
    builder.Services.AddScoped<Modgud.Infrastructure.OpenIddict.IRefreshTokenReuseObserver>(sp =>
        sp.GetRequiredService<Modgud.Authentication.Sessions.ClientSessionService>());
    builder.Services.AddScoped<Modgud.Authentication.Gdpr.IGdprService,
        Modgud.Authentication.Gdpr.GdprService>();

    // User-lifecycle access "kill switch" — revokes OAuth grants + sessions +
    // security stamp on delete/deactivate/force-logout. The OAuth half lives in
    // Infrastructure so the Authentication slice stays OpenIddict-free.
    // The interfaces resolve to apply-scope-aware Deferring* decorators (ADR-0005
    // Phase 0): outside a TenantApplyTransaction they pass straight through; inside
    // one they defer the cascade until after the apply committed. The concrete
    // implementations stay registered — the deferred replay re-resolves them.
    builder.Services.AddScoped<Modgud.Infrastructure.OpenIddict.OpenIddictGrantRevoker>();
    builder.Services.AddScoped<Modgud.Infrastructure.OpenIddict.IOAuthGrantRevoker,
        Modgud.Infrastructure.OpenIddict.DeferringOAuthGrantRevoker>();
    builder.Services.AddScoped<Modgud.Authentication.Sessions.UserAccessRevoker>();
    builder.Services.AddScoped<Modgud.Authentication.Sessions.IUserAccessRevoker,
        Modgud.Authentication.Sessions.DeferringUserAccessRevoker>();
    // MG-FT-07 — the staffing-session kill switch: locks + every revocation
    // cascade (user/passkey/grant/terminal/position) end sessions through it.
    builder.Services.AddScoped<Modgud.Infrastructure.PositionTerminals.StaffingRevoker>();
    builder.Services.AddScoped<Modgud.Infrastructure.PositionTerminals.IStaffingRevoker,
        Modgud.Infrastructure.PositionTerminals.DeferringStaffingRevoker>();
    builder.Services.AddScoped<Modgud.Api.Features.Auth.Staffing.IActivationProof,
        Modgud.Api.Features.Auth.Staffing.PersonalPasskeyActivationProof>();
    builder.Services.AddScoped<Modgud.Api.Features.Auth.Staffing.IActivationProof,
        Modgud.Api.Features.Auth.Staffing.PersonalPasswordActivationProof>();
    builder.Services.AddScoped<Modgud.Api.Features.Auth.Staffing.IActivationProof,
        Modgud.Api.Features.Auth.Staffing.PersonalEmailOtpActivationProof>();
    builder.Services.AddScoped<Modgud.Api.Features.Auth.Staffing.IActivationProof,
        Modgud.Api.Features.Auth.Staffing.PositionTokenActivationProof>();
    builder.Services.AddScoped<Modgud.Api.Features.Auth.Staffing.ActivationInvalidationRegistry>();
    builder.Services.AddScoped<Modgud.Api.Features.Auth.Staffing.ActivationProofRegistry>();

    // Infrastructure (Marten + repositories + query services + event dispatcher)
    // Authentication Marten setup (documents + events + projections) is wired via
    // UseModgudAuthentication() so Infrastructure stays unaware of Authentication.
    // OAuth admin slice (clients/scopes/APIs/login providers) is wired here too —
    // it has no separate slice project yet so the wiring lives directly in Infrastructure.
    builder.Services.AddInfrastructure(conf.DbSettings.ConnectionString,
        options =>
        {
            options.UseModgudAuthentication();
            options.UseModgudOAuth();
            options.UseModgudPositionTerminals();
            options.Events.Subscribe(new Modgud.Api.Features.ChangeFeed.AppChangeFeedSubscription());
        },
        hosting: clusterHosting);

    // OpenTelemetry foundation (Phase 1). See
    // the maintainers' 'observability-opentelemetry' design note.
    var observabilitySettings = configManager.GetConfig<ObservabilitySettings>();
    builder.Services.AddModgudObservability(
        observabilitySettings,
        conf.DbSettings.ConnectionString);

    // Per-tenant DataProtection. Each realm's keys live in that realm's
    // database — a master-DB compromise yields no cookie-forgery for any
    // tenant, and a tenant-DB compromise is contained to that tenant.
    // Cookies + antiforgery survive `docker-compose down && up` as a
    // free side effect (no more login-everyone-out on deploy).
    // See HA-2a in
    // the maintainers' 'ha-multi-instance' design note.
    //
    // Audit M7: optionally encrypt each realm's key ring at rest with an
    // operator-supplied certificate (DataProtection__CertificatePath, optional
    // ...Password). Opt-in + non-breaking — without it the ring is unencrypted
    // in the tenant DB (the per-realm DB partition is the only boundary), exactly
    // as before. The operator owns the cert lifecycle; mixing is safe (existing
    // unencrypted keys stay readable, only new keys are wrapped).
    var dpCertPath = Environment.GetEnvironmentVariable("DataProtection__CertificatePath");
    System.Security.Cryptography.X509Certificates.X509Certificate2? dpCert = null;
    if (!string.IsNullOrWhiteSpace(dpCertPath) && File.Exists(dpCertPath))
    {
        var dpCertPassword = Environment.GetEnvironmentVariable("DataProtection__CertificatePassword");
        dpCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader
            .LoadPkcs12FromFile(dpCertPath, dpCertPassword);
        Log.Information(
            "DataProtection key ring will be encrypted at rest with the operator certificate at {Path}", dpCertPath);
    }
    else if (!builder.Environment.IsDevelopment())
    {
        Log.Warning(
            "DataProtection key ring is NOT encrypted at rest — set DataProtection__CertificatePath to an " +
            "operator certificate to protect login-provider secrets, SAML SP keys and auth cookies against a " +
            "tenant-DB dump. The per-realm DB partition is the only boundary until then.");
    }
    builder.Services.AddTenantedDataProtection(dpCert);

    // OpenIddict OAuth 2.0 / OIDC server — uses our custom Marten stores. Settings are
    // captured at config time so signing certs / lifetimes can be pinned before the
    // host is built. Per-realm issuer is applied at request time via RealmIssuerHandler.
    var openIddictSettings = configManager.GetConfig<OpenIddictSettings>();

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
        CertificateBootstrap.EnsureSigningCertificateExists(openIddictSettings);
        CertificateBootstrap.EnsureEncryptionCertificateExists(openIddictSettings);
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

        // No issuer check here: the issuer is per-realm and request-derived (the
        // realm's host via BaseUri), not a global setting — see
        // RealmIssuerHandler / RealmSigningKeyHandler / RealmTokenValidationHandler.
        // The real "don't advertise a wrong issuer" protection is the realm's
        // configured domain plus the reverse proxy pinning the forwarded Host
        // (ProxyAllowedNetworks), neither of which is a boot-time config value.

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

    // CORS for browser-only SPA clients (Authorization Code + PKCE in the
    // browser, no BFF). OAuthCorsMiddleware emits headers on the OIDC endpoints,
    // echoing only origins registered on a client in the active realm. See
    // Modgud.Api.Cors.
    builder.Services.AddCors();
    builder.Services.AddMemoryCache();
    builder.Services.AddScoped<Modgud.Api.Cors.IClientCorsOriginProvider, Modgud.Api.Cors.ClientCorsOriginProvider>();

    // Migration services for legacy Modgud data have been removed in the
    // IdP-only baseline — no historical documents to upgrade to event streams.

    // Wolverine CQRS + Marten projection side effects.
    //
    // ADR 0010 (D3) — the environment decides, not an env var: Production runs
    // Balanced (leader election over the node table in the master DB, outbox
    // agents and projection shards assigned across live nodes, stale nodes
    // recovered), Development and Testing run Solo. Two Solo instances would
    // both drain the same outbox row; that shape no longer exists for Production.
    var wolverineMode = clusterHosting.IsWolverineManaged
        ? DurabilityMode.Balanced
        : DurabilityMode.Solo;
    Log.Information(
        "Cluster coordination: {Coordination} — Wolverine {Mode}, projections {Projections}, Quartz {Quartz}, node {Node}",
        clusterHosting.Coordination,
        wolverineMode,
        clusterHosting.IsWolverineManaged ? "Wolverine-managed" : "Marten daemon (" + clusterHosting.SingleProcessDaemonMode + ")",
        clusterHosting.IsWolverineManaged ? "clustered Postgres store" : "in-memory",
        clusterHosting.NodeName);

    builder.Host.UseWolverine(opts =>
    {
        opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
        opts.Discovery.IncludeAssembly(typeof(Modgud.Authentication.Api.Admin.RecoveryCli).Assembly);
        opts.Discovery.IncludeAssembly(typeof(Modgud.Authorization.Commands.CreateGroupCommand).Assembly);
        opts.Durability.Mode = wolverineMode;
        // Production generates handler code in-memory (Dynamic): each handler is
        // compiled via Roslyn on first use and never written to disk. The previous
        // Auto mode tried to persist the generated source under /app/Internal —
        // which the non-root container user cannot write — logging "Access to the
        // path '/app/Internal' is denied" on the first hit of every handler.
        // Dev/Test keep Auto, which writes into Internal/Generated/ on a writable
        // working tree and reuses it across restarts.
        //
        // (Pre-generating at build time + TypeLoadMode.Static would also avoid the
        // runtime Roslyn pass, but it requires JasperFx's `RunJasperFxCommands`
        // entry point, which is incompatible with this app's WebApplicationFactory
        // integration tests — see git history for fix/wolverine-codegen-static-prod.)
        opts.CodeGeneration.TypeLoadMode = builder.Environment.IsProduction()
            ? JasperFx.CodeGeneration.TypeLoadMode.Dynamic
            : JasperFx.CodeGeneration.TypeLoadMode.Auto;

        // Wolverine 6 made `ServiceLocationPolicy.NotAllowed` the default. We
        // keep that strict default — accidental new service-location dependencies
        // fail loudly at codegen — and document each known exception below.

        // ASP.NET Identity — UserManager<T>/SignInManager<T> take IServiceProvider
        // in their constructors by design (IPasswordHasher<T>/IUserValidator<T>
        // resolution). Not refactorable without forking Identity.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<Microsoft.AspNetCore.Identity.UserManager<Modgud.Authentication.Domain.ApplicationUser>>();
        opts.CodeGeneration.AlwaysUseServiceLocationFor<Microsoft.AspNetCore.Identity.SignInManager<Modgud.Authentication.Domain.ApplicationUser>>();

        // Cocoar.JsEval module-builder boundary — JsEval 4.1 collapsed the
        // previous transitive service-location entries (IMembershipEvaluator,
        // IAutoMembershipRecalculator) into a single one at the JsEngine's
        // module-builder seam.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<Cocoar.JsEval.IJsModuleBuilder>();

        // User-lifecycle access kill switch — its dependency chain reaches the
        // OpenIddict managers (IOpenIddictTokenManager/AuthorizationManager),
        // which OpenIddict registers as opaque scoped lambda factories Wolverine
        // can't construct in generated code. DeleteUsersCommand injects it.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<Modgud.Authentication.Sessions.IUserAccessRevoker>();

        // MG-FT-07 staffing-session kill switch — same story: it composes
        // IOAuthGrantRevoker, whose chain reaches the same opaque OpenIddict
        // manager factories. DeleteUsersCommand injects it for the user cascade.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<Modgud.Infrastructure.PositionTerminals.IStaffingRevoker>();

        // Auto-register Event Forwarding subscriptions for all ReferenceSyncHandler<TEvent> implementations
        ReferenceSyncRegistration.RegisterAll(opts, typeof(Program).Assembly);

    });

    // ADR 0009 — the fan-out reads the end markers from the event store itself (a
    // Marten subscription driven by the projection daemon, in order, with durable
    // progress), not from session-commit forwarding: sessions end from plain
    // endpoints whose Marten session is not Wolverine's outboxed one.
    builder.Services.ProcessEventsWithWolverineHandlersInStrictOrder(
        Modgud.Authentication.BackChannelLogout.BackChannelLogoutConstants.SubscriptionName,
        subscription =>
        {
            subscription.IncludeType<Modgud.Authentication.Events.UserAccessEndedEvent>();
            subscription.Options.SubscribeFromPresent();
        });

    // Structured best-effort security-event sink. Realm events are routed to the
    // owning physical realm DB; PII-free deployment events go to the Global Store.
    builder.Services.AddSingleton<Modgud.Infrastructure.Audit.SecurityAuditLog>();
    builder.Services.AddSingleton<Modgud.Infrastructure.Audit.ISecurityAuditLog>(
        sp => sp.GetRequiredService<Modgud.Infrastructure.Audit.SecurityAuditLog>());
    builder.Services.AddHostedService<Modgud.Infrastructure.Audit.SecurityAuditWriter>();

    // Quartz-based scheduling framework. Realm jobs get one independent
    // Quartz job + trigger per realm; deployment-wide jobs are registered once
    // and are visible only from the current Control-Plane realm.
    builder.Services.AddScheduling(new SchedulingStoreOptions
    {
        // ADR 0010 (D4) — clustered Postgres job store in the master DB; the
        // schema is created by QuartzSchemaBootstrap before the scheduler starts.
        PersistentStoreConnectionString = clusterHosting.IsWolverineManaged
            ? conf.DbSettings.ConnectionString
            : null,
    });
    builder.Services.AddRealmJob<Modgud.Api.Features.Admin.Jobs.JobRunHistoryRetentionJob>(
        key: Modgud.Api.Features.Admin.Jobs.JobRunHistoryRetentionJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.JobRunHistoryRetentionJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.JobRunHistoryRetentionJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.JobRunHistoryRetentionJob.Description,
        getParameterSchema: Modgud.Api.Features.Admin.Jobs.JobRunHistoryRetentionJob.GetParameterSchema);
    builder.Services.AddRealmJob<Modgud.Api.Features.Admin.Jobs.DcrGcJob>(
        key: Modgud.Api.Features.Admin.Jobs.DcrGcJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.DcrGcJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.DcrGcJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.DcrGcJob.Description);
    // ADR 0006 — pending-registration hygiene + legacy ghost clean-up (dry-run by default).
    builder.Services.AddRealmJob<Modgud.Api.Features.Admin.Jobs.PendingRegistrationSweepJob>(
        key: Modgud.Api.Features.Admin.Jobs.PendingRegistrationSweepJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.PendingRegistrationSweepJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.PendingRegistrationSweepJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.PendingRegistrationSweepJob.Description);
    builder.Services.AddRealmJob<Modgud.Api.Features.Admin.Jobs.UnconfirmedRegistrationReaperJob>(
        key: Modgud.Api.Features.Admin.Jobs.UnconfirmedRegistrationReaperJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.UnconfirmedRegistrationReaperJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.UnconfirmedRegistrationReaperJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.UnconfirmedRegistrationReaperJob.Description,
        getParameterSchema: Modgud.Api.Features.Admin.Jobs.UnconfirmedRegistrationReaperJob.GetParameterSchema);
    builder.Services.AddRealmJob<Modgud.Api.Features.Admin.Jobs.StaffingSweepJob>(
        key: Modgud.Api.Features.Admin.Jobs.StaffingSweepJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.StaffingSweepJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.StaffingSweepJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.StaffingSweepJob.Description);
    builder.Services.AddRealmJob<Modgud.Api.Features.Inbox.InboxRetentionJob>(
        key: Modgud.Api.Features.Inbox.InboxRetentionJob.Key,
        name: Modgud.Api.Features.Inbox.InboxRetentionJob.Name,
        defaultCron: Modgud.Api.Features.Inbox.InboxRetentionJob.DefaultCron,
        description: Modgud.Api.Features.Inbox.InboxRetentionJob.Description);
    builder.Services.AddRealmJob<Modgud.Api.Features.Admin.Jobs.AccountLifecycleSweepJob>(
        key: Modgud.Api.Features.Admin.Jobs.AccountLifecycleSweepJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.AccountLifecycleSweepJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.AccountLifecycleSweepJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.AccountLifecycleSweepJob.Description);
    builder.Services.AddRealmJob<Modgud.Api.Features.Admin.Jobs.SessionPruneJob>(
        key: Modgud.Api.Features.Admin.Jobs.SessionPruneJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.SessionPruneJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.SessionPruneJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.SessionPruneJob.Description);
    builder.Services.AddRealmJob<Modgud.Api.Features.Admin.Jobs.BackChannelLogoutRetryJob>(
        key: Modgud.Api.Features.Admin.Jobs.BackChannelLogoutRetryJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.BackChannelLogoutRetryJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.BackChannelLogoutRetryJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.BackChannelLogoutRetryJob.Description);
    builder.Services.AddRealmJob<Modgud.Api.Features.Admin.Jobs.SigningKeyJanitorJob>(
        key: Modgud.Api.Features.Admin.Jobs.SigningKeyJanitorJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.SigningKeyJanitorJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.SigningKeyJanitorJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.SigningKeyJanitorJob.Description,
        // Soft-delete keeps the tenant DB and its private key material. This
        // realm-owned hygiene therefore continues while the realm is inactive.
        runWhenRealmInactive: true);
    builder.Services.AddSystemJob<Modgud.Api.Features.Admin.Jobs.SystemJobRunHistoryRetentionJob>(
        key: Modgud.Api.Features.Admin.Jobs.SystemJobRunHistoryRetentionJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.SystemJobRunHistoryRetentionJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.SystemJobRunHistoryRetentionJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.SystemJobRunHistoryRetentionJob.Description,
        getParameterSchema: Modgud.Api.Features.Admin.Jobs.JobRunHistoryRetentionJob.GetParameterSchema);
    builder.Services.AddRealmJob<Modgud.Api.Features.Admin.Jobs.SecurityAuditPruneJob>(
        key: Modgud.Api.Features.Admin.Jobs.SecurityAuditPruneJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.SecurityAuditPruneJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.SecurityAuditPruneJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.SecurityAuditPruneJob.Description);
    builder.Services.AddSystemJob<Modgud.Api.Features.Admin.Jobs.PlatformAuditPruneJob>(
        key: Modgud.Api.Features.Admin.Jobs.PlatformAuditPruneJob.Key,
        name: Modgud.Api.Features.Admin.Jobs.PlatformAuditPruneJob.Name,
        defaultCron: Modgud.Api.Features.Admin.Jobs.PlatformAuditPruneJob.DefaultCron,
        description: Modgud.Api.Features.Admin.Jobs.PlatformAuditPruneJob.Description,
        getParameterSchema: Modgud.Api.Features.Admin.Jobs.PlatformAuditPruneJob.GetParameterSchema);

    // Inbox — per-recipient notifications with SignalR live push. Both
    // services are scoped (tenant-aware IDocumentSession). The InboxHub
    // is auto-discovered by SignalARRR via Program assembly scan.
    builder.Services.AddScoped<Modgud.Application.Inbox.IInboxNotifier,
        Modgud.Infrastructure.Inbox.InboxNotifier>();
    builder.Services.AddScoped<Modgud.Application.Inbox.IInboxRetentionService,
        Modgud.Infrastructure.Inbox.InboxRetentionService>();
    // Override the Infrastructure-side no-op binding for IJobRunNotifier with
    // the real Inbox-driven implementation. JobRunListener resolves this from
    // the scope it opens per execution; failures notify admins, manual-trigger
    // completions notify the triggering user.
    builder.Services.AddScoped<Modgud.Infrastructure.Scheduling.IJobRunNotifier,
        Modgud.Api.Features.Inbox.JobRunNotifier>();

    // Phase 5 — in-app per-realm live error feed (§B.3). One process-local
    // buffer with an independently-capped ring PER realm (not a global ring —
    // a noisy realm must not be able to evict a quiet realm's errors). The
    // hub (ObservabilityHub.LogsSubscribe) and the /observability/errors
    // endpoint read this same singleton; the ErrorFeedSink below feeds it.
    var errorFeed = observabilitySettings.ErrorFeed;
    var errorFeedBuffer = new Modgud.Infrastructure.Observability.RealmErrorBuffer(
        errorFeed.CapacityPerRealm);
    builder.Services.AddSingleton(errorFeedBuffer);

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

        // Stamp every event with the ambient realm slug (RealmLogEnricher). Kept
        // after the "Auth:" sink was retired: it is how operational logs carry their
        // realm tag for Console/File and for the OTLP log export (Phase 4) below.
        logConfig.Enrich.With(new Modgud.Authentication.AuthLog.RealmLogEnricher());

        // Console + File
        logConfig.WriteTo.Console(theme: AnsiConsoleTheme.Code);

        if (!string.IsNullOrWhiteSpace(conf.Logging.LogPath))
        {
            var path = PathHelper.GetFullPath(conf.Logging.LogPath);
            path = Path.Combine(path, "log.log");
            logConfig.WriteTo.File(path, rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31);
        }

        // Phase 4 — OTLP log export. Off by default; shares the metrics/tracing
        // OTLP gate + endpoint (Observability__Otlp__Enabled / OtlpSettings), so a
        // deployment without a collector/OpenObserve is unaffected (§B.0). Wired as
        // a Serilog sink rather than OTel .WithLogs(): AddSerilog runs with
        // writeToProviders:false, so an OTel ILoggerProvider would never see the
        // Serilog enrichers — in particular the RealmLogEnricher tag that §B.1
        // requires. The sink emits every Serilog property (incl. Realm) as a
        // log-record attribute and reads Activity.Current for trace/span
        // correlation automatically. The redaction GUARANTEE lives at the collector,
        // not here; LogPiiMasking stays as belt. Endpoint is a bare base host:port
        // for both protocols — the sink derives the per-signal path itself (and
        // trims any /v1/logs an operator appends).
        // See the maintainers' 'logging-audit-redesign' design note §B.1-B.2.
        if (observabilitySettings.Otlp.Enabled)
        {
            var otlp = observabilitySettings.Otlp;
            logConfig.WriteTo.OpenTelemetry(o =>
            {
                o.Endpoint = otlp.Endpoint;
                o.Protocol = otlp.Protocol.Equals("HttpProtobuf", StringComparison.OrdinalIgnoreCase)
                    ? OtlpProtocol.HttpProtobuf
                    : OtlpProtocol.Grpc;
                o.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = observabilitySettings.ServiceName,
                    ["service.version"] = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetName().Version?.ToString() ?? "unknown",
                    ["service.instance.id"] = Environment.MachineName,
                };
            });
        }

        // Phase 5 — in-app per-realm error feed sink (§B.3). Local-only, behind
        // its own flag (default on; no external dependency). Captures Error+
        // events from Modgud.* loggers (configurable level/prefix — Open
        // Decision #7) into the per-realm RealmErrorBuffer. Sits AFTER the
        // RealmLogEnricher above, so each entry carries its realm tag. The
        // collector redaction does NOT cover this in-app path — the call-site
        // PII belt + per-realm read scoping are the controls (mirrors the
        // streamless security store).
        if (errorFeed.Enabled)
        {
            var minimumLevel =
                Enum.TryParse<Serilog.Events.LogEventLevel>(errorFeed.MinimumLevel, ignoreCase: true, out var lvl)
                    ? lvl
                    : Serilog.Events.LogEventLevel.Error;
            logConfig.WriteTo.Sink(new Modgud.Authentication.AuthLog.ErrorFeedSink(
                errorFeedBuffer, minimumLevel, errorFeed.SourcePrefix));
        }
    });

    // Must run after EVERY registration: AddTenantedDataProtection above already
    // strips this, but OpenIddict's builder calls AddDataProtection() and the
    // TryAddEnumerable puts the startup check back. See the method's remarks.
    builder.Services.RemoveRootKeyRingStartupCheck();

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
    app.UseMiddleware<Modgud.Api.Middleware.SecurityHeadersMiddleware>();

    // Short-circuit attack-probe paths (.git, .env, /server-status, /wp-*, …)
    // with a clean 404 instead of falling through to the SPA fallback that
    // would otherwise return index.html with 200 for any unmatched path.
    // Closes scanner-noise findings ("/.git/config returns 200") without
    // changing real exposure surface — the SPA fallback was never leaking
    // data, but the 200 is misread by automated reports.
    app.UseMiddleware<Modgud.Api.Middleware.WellKnownAttackPathsMiddleware>();

    // Enable OpenAPI endpoint (not in production)
    if (!app.Environment.IsProduction())
    {
        app.MapOpenApi();
    }

    app.AddLogging();

    // Static SPA files are deployment assets, not realm data. Serve them before
    // tenant resolution so the first-installation UI can load while zero realms
    // exist. The fallback endpoint registered here is executed by the
    // realm-independent branch below for /install.
    app.UseSpaUI();

    app.UseRouting();

    // A fresh deployment intentionally has no realm. Keep every normal route
    // closed until the shell-authorized installation API creates the first one.
    app.UseMiddleware<InstallationGateMiddleware>();

    // The installation API must be able to run before a realm -- and therefore
    // before realm-scoped cookies, DataProtection keys and Marten sessions --
    // exist. Give it a terminal branch that only runs endpoint routing and the
    // endpoint rate limiter. Normal realm/auth middleware can never be resolved
    // from this branch.
    app.MapWhen(
        context => RealmIndependentPathPolicy.Matches(context.Request.Path),
        realmIndependentBranch =>
        {
            realmIndependentBranch.Run(async context =>
            {
                var endpoint = context.GetEndpoint();
                if (!RealmIndependentPathPolicy.CanExecute(endpoint, context.Request.Path))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await endpoint!.RequestDelegate!(context);
            });
        });

    // Resolve tenant from the Host header BEFORE auth runs so the
    // TenantedSessionFactory sees the correct tenant for every Marten session
    // opened during authentication / authorization (e.g. Identity user lookup).
    app.UseMiddleware<RealmMiddleware>();
    app.UseMiddleware<TenantContextMiddleware>();

    // CORS for the browser-reachable OIDC endpoints (token / userinfo /
    // revocation + the public discovery/JWKS metadata). Runs after the tenant
    // is resolved (so the per-realm Allowed-CORS-Origins lookup works) and
    // before the control-plane gate / auth so a preflight OPTIONS is answered
    // with 204 without authentication. See Modgud.Api.Cors.OAuthCorsMiddleware.
    app.UseMiddleware<Modgud.Api.Cors.OAuthCorsMiddleware>();

    // C14 — Control-Plane / Data-Plane separation. Runs after RealmMiddleware
    // (so TenantInfo is on HttpContext) and before authentication so that
    // realm-management routes are 404-hidden from tenant hosts even before
    // the cookie is inspected.
    app.UseMiddleware<Modgud.Api.Middleware.ControlPlaneGateMiddleware>();

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<Modgud.Authentication.Api.Account.TwoFactorEnforcementMiddleware>();

    // CSRF defence (C6 — CSRF-02 / CSRF-03). Runs after authentication so the
    // browser's cookie has already been resolved (we want auth to be the gate
    // for who you are; CSRF middleware is the gate for "did the request come
    // from this origin"). Targets only state-changing /api/* requests; OAuth
    // endpoints (/connect/*) have their own protocol-level protections.
    app.UseMiddleware<Modgud.Api.Middleware.CsrfDefenseMiddleware>();

    // RATE-01 — apply the rate-limit policies registered in
    // AddRateLimiter. Endpoints opt in via .RequireRateLimiting("policy")
    // (see /connect/* + /api/account/bootstrap-admin + /api/account/forgot-password +
    // /api/account/magic-link below). Endpoints without an explicit
    // policy are not rate-limited at the app layer.
    // Resolve the realm's configured auth rate-limit ceilings and stash them on
    // HttpContext.Items BEFORE the limiter runs, so the (synchronous) policy
    // factories can read per-realm limits. Runs after RealmMiddleware (tenant set).
    app.UseMiddleware<Modgud.Api.Middleware.AuthCallerContextMiddleware>();

    // Observability surface: /metrics (Prometheus scrape) + /health/live +
    // /health/ready. AllowAnonymous applied inside the helper. Operator
    // must keep /metrics off the public internet — bind via reverse-proxy
    // ACL or localhost-only listener.
    app.MapModgudObservability(observabilitySettings);
    app.MapInstallationEndpoints();


    // OpenIddict OAuth/OIDC endpoints (/connect/authorize, /token, /userinfo, /logout, /consent).
    // OpenIddict's middleware is registered as part of UseOpenIddict... hooks called by
    // ASP.NET Core during AddOpenIddict. The discovery + JWKS endpoints are auto-mapped
    // by OpenIddict; only the passthrough endpoints (authorize/token/userinfo/...) need
    // explicit minimal-API handlers.
    app.MapAuthorizationEndpoints();
    app.MapConsentEndpoints();
    app.MapDeviceVerificationEndpoints();
    app.MapDcrRegistrationEndpoints();

    app.MapStatusEndpoints();
    app.MapAuthLogEndpoints("api");
    app.MapAuditEndpoints("api");
    app.MapAppSettingsEndpoints("api");
    app.MapProjectionEndpoints("api");
    app.MapRealmsEndpoints("api");
    app.MapRealmConfigEndpoints("api");
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
    app.MapCustomizationCompositionsEndpoints("api");
    Modgud.Api.Features.Admin.Jobs.JobsEndpoints.MapJobsEndpoints(app, "api");
    Modgud.Api.Features.Inbox.InboxEndpoints.MapInboxEndpoints(app, "api");
    Modgud.Api.Features.Inbox.InboxSettingsEndpoints.MapInboxSettingsEndpoints(app, "api");

    // Account & Setup Endpoints (have additional strict "auth" rate limit)
    app.MapAccountEndpoints("api");
    app.MapProfileEndpoints("api");
    app.MapMfaEndpoints("api");
    app.MapEmailOtpEndpoints("api");
    app.MapNativeOtpEndpoints("api");
    app.MapNativeRegisterEndpoints("api");
    app.MapPasskeyEndpoints("api");
    app.MapNativePasskeyEndpoints();
    app.MapNativePasskeyEnrollEndpoints();
    Modgud.Api.Features.Auth.Staffing.StaffingEndpoints.MapStaffingEndpoints(app);
    app.MapNativePasskeyManagementEndpoints();
    app.MapMagicLinkEndpoints("api");
    app.MapPasswordResetEndpoints("api");
    app.MapEmailVerificationEndpoints("api");
    app.MapRegisterEndpoints("api");
    app.MapRealmSettingsEndpoints("api");
    app.MapBootstrapEndpoints("api");
    app.MapSessionEndpoints("api");
    app.MapGdprEndpoints("api");
    app.MapExternalAuthEndpoints("api");
    app.MapSamlEndpoints();
    app.MapProfileLinkEndpoints("api");
    app.MapLoginProvidersEndpoints("api");
    app.MapUserUpdateScriptTestEndpoint("api");

    // Marten Endpoints
    app.MapUsersEndpoints("api");
    Modgud.Api.Features.ServiceAccounts.ServiceAccountsEndpoints.MapServiceAccountsEndpoints(app, "api");
    Modgud.Api.Features.Positions.PositionsEndpoints.MapPositionsEndpoints(app, "api");
    Modgud.Api.Features.Positions.PositionGrantsEndpoints.MapPositionGrantsEndpoints(app, "api");
    Modgud.Api.Features.Positions.PositionTerminalsEndpoints.MapPositionTerminalsEndpoints(app, "api");
    Modgud.Api.Features.Positions.ActivationTokenEndpoints.MapActivationTokenEndpoints(app);
    app.MapPrincipalEndpoints("api");
    app.MapRolesEndpoints("api");
    app.MapGroupEndpoints("api");
    // An App is one resource: AppsEndpoints carries the per-App ADR-0011 settings override
    // inline (POST/PUT/GET /api/app), so there is no separate /settings endpoint.
    Modgud.Api.Features.Admin.Apps.AppsEndpoints.MapAppsEndpoints(app, "api");
    Modgud.Api.Features.ChangeFeed.AppChangeFeedEndpoints.MapAppChangeFeedEndpoints(app, "api");
    // ADR-0012 — app-scoped invite codes (dual-auth: invite:write scope or invite-code:write permission).
    Modgud.Api.Features.InviteCodes.InviteCodeEndpoints.MapInviteCodeEndpoints(app, "api");

    // /api/v1/me/* — Cookie-only, for the admin SPA's self-introspection.
    Modgud.Api.Features.Auth.MeEndpoints.MapMeEndpoints(app, "api");

    // End-user VitePress documentation at /docs — auth-gated, redirect to /login on unauth.
    // MUST be BEFORE app.UseEndpoints — otherwise the SPA fallback endpoint (registered
    // inside UseSpaUI) terminates the pipeline here and swallows /docs/* requests.
    app.UseDocs();

    app.UseEndpoints(e => { });

    app.MapSignalARRRHub<UIHub>("/signalr/ui");

    // ResourceRegistry is now instance-based and configured via AddModgudAuthorization
    // in AddInfrastructure — no static init required.

    // Enable SignalR side effects only after Wolverine is ready
    // (prevents WolverineHasNotStartedException during daemon catchup on startup)
    app.Lifetime.ApplicationStarted.Register(() =>
        Modgud.Infrastructure.Events.ProjectionSideEffects.Enabled = true);

    // ────────────────────────────────────────────────────────────────────────
    //  Multi-tenant bootstrap. A fresh deployment intentionally has ZERO
    //  realms. Only the master database + Global Store are prepared here; the
    //  shell-authorized installation flow provisions the first ordinary realm.
    //  Existing realms are schema-applied and idempotently seeded on every boot.
    // ────────────────────────────────────────────────────────────────────────
    var mainCs = conf.DbSettings.ConnectionString;
    var bootstrapBuilder = new NpgsqlConnectionStringBuilder(mainCs);
    var baseDbName = bootstrapBuilder.Database
        ?? throw new InvalidOperationException("DbSettings.ConnectionString is missing 'Database='");

    bootstrapBuilder.Database = "postgres";

    // First contact with PostgreSQL. In a container stack Modgud regularly
    // comes up before Postgres does; wait a bounded window for a transient
    // failure (refused / DNS / "starting up"), fail at once on a
    // configuration error, and terminate after the window. Kestrel is not
    // listening yet, so nothing is served half-booted.
    var startupWindow = TimeSpan.FromSeconds(Math.Max(0, conf.DbSettings.StartupTimeoutSeconds));
    await StartupDatabaseWait.RunAsync(
        async ct =>
        {
            await using var bootstrapConn = new NpgsqlConnection(bootstrapBuilder.ConnectionString);
            await bootstrapConn.OpenAsync(ct);
            await using var checkCmd = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @dbName", bootstrapConn);
            checkCmd.Parameters.AddWithValue("@dbName", baseDbName);
            if (await checkCmd.ExecuteScalarAsync(ct) is null)
            {
                var quotedName = "\"" + baseDbName.Replace("\"", "\"\"") + "\"";
#pragma warning disable CA2100
                await using var createCmd = new NpgsqlCommand(
                    $"CREATE DATABASE {quotedName}", bootstrapConn);
#pragma warning restore CA2100
                await createCmd.ExecuteNonQueryAsync(ct);
                Log.Information("Created master database {DbName}", baseDbName);
            }
        },
        startupWindow,
        onRetry: (attempt, wait, ex) => Log.Warning(
            "PostgreSQL at {Host}:{Port} not reachable yet (attempt {Attempt}: {Reason}) - retrying in {Wait}s, giving up after {Window}s",
            bootstrapBuilder.Host, bootstrapBuilder.Port, attempt, ex.Message, (int)wait.TotalSeconds, (int)startupWindow.TotalSeconds));

    // The primary store owns the tenant registry and all registered realm DBs.
    // The Global Store owns deployment-wide state, including the realm registry
    // and first-installation challenges.
    var store = app.Services.GetRequiredService<Marten.IDocumentStore>();
    await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    var globalStore = app.Services.GetRequiredService<IGlobalStore>();
    await globalStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

    // ADR 0010 (D4) — Quartz validates its tables at start and never creates
    // them; do it here, once, under a cluster lock (two nodes may boot together).
    if (clusterHosting.IsWolverineManaged)
    {
        await QuartzSchemaBootstrap.EnsureAsync(
            conf.DbSettings.ConnectionString,
            app.Services.GetRequiredService<IClusterLock>(),
            app.Services.GetRequiredService<ILogger<Program>>());
    }

    // The data-event relay is fire-and-forget by contract; surface its failures.
    app.Services.GetRequiredService<DataEventDispatcher>().RelayFailed += (ev, ex) =>
        Log.Warning(ex, "Data-event relay failed for {Subject}/{Action}", ev.Subject, ev.Action);

    using (var realmScope = app.Services.CreateScope())
    {
        var realmService = realmScope.ServiceProvider.GetRequiredService<IRealmProvisioningService>();
        var configuredRealms = (await realmService.GetAllRealmsAsync())
            .Where(r => r.IsActive)
            .OrderBy(r => r.CreatedAt)
            .ToList();
        var startupLogger = realmScope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        foreach (var realm in configuredRealms)
        {
            await Modgud.Infrastructure.OAuth.OAuthRealmSeeder.SeedAsync(
                realmScope.ServiceProvider, realm.Slug, startupLogger);
            await realmScope.ServiceProvider
                .GetRequiredService<Modgud.Application.Services.ILoginProviderRealmSeeder>()
                .SeedAsync(realm.Slug, startupLogger);
            await Modgud.Infrastructure.Authorization.AppRealmSeeder.SeedAsync(
                realmScope.ServiceProvider,
                realm.Slug,
                isControlPlane: realm.IsControlPlane,
                startupLogger);
        }

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
        // here, against the first active tenant. Marten caches LINQ→SQL per
        // DocumentStore, not per tenant — so warming with one tenant is
        // enough for all tenants. Costs: ~2-3s extra at boot, then no
        // user-visible cliff for the rest of the host's lifetime.
        try
        {
            // IGlobalStore — realm-admin queries.
            await realmService.GetAllRealmsAsync();
            var warmupRealm = configuredRealms.FirstOrDefault();
            if (warmupRealm is not null)
            {
                await realmService.GetRealmBySlugAsync(warmupRealm.Slug);
                using (TenantContext.Enter(warmupRealm.Slug))
                await using (var session = realmScope.ServiceProvider
                    .GetRequiredService<Marten.IDocumentStore>().QuerySession(warmupRealm.Slug))
                {
                    await session.Query<Modgud.Authentication.Domain.ApplicationUser>()
                        .Where(u => !u.IsDeleted).Take(1).ToListAsync();
                    await session.Query<Modgud.Infrastructure.Persistence.Marten.Projections.Users.UserView>()
                        .Where(u => !u.IsDeleted).OrderBy(u => u.UserName).Take(1).ToListAsync();
                    await session.Query<Modgud.Authorization.Principals.Principal>()
                        .Where(p => !p.IsDeleted).Take(1).ToListAsync();
                    await session.Query<Modgud.Authorization.Roles.PermissionRole>()
                        .Where(r => !r.IsDeleted).Take(1).ToListAsync();
                    await session.Query<Modgud.Authorization.Principals.Group>()
                        .Where(g => !g.IsDeleted).Take(1).ToListAsync();
                    await session.Query<Modgud.Authentication.Domain.LoginProviders.LoginProvider>()
                        .Where(p => !p.IsDeleted).Take(1).ToListAsync();
                    await session.Query<Modgud.Infrastructure.Audit.RealmSecurityAuditEvent>()
                        .OrderByDescending(l => l.Timestamp).Take(1).ToListAsync();
                    await session.Query<Modgud.Authentication.Domain.UserChangeRequest>()
                        .Take(1).ToListAsync();
                }

                using (TenantContext.Enter(warmupRealm.Slug))
                using (var oauthWarmupScope = app.Services.CreateScope())
                {
                    // OAuthAdminService owns a scoped IDocumentSession. Create and
                    // resolve its scope only after the ambient tenant is active;
                    // otherwise TenantedSessionFactory correctly rejects the
                    // tenant-scoped write session during application startup.
                    var oauthAdmin = oauthWarmupScope.ServiceProvider
                        .GetRequiredService<Modgud.Application.Services.OAuthAdminService>();
                    await oauthAdmin.GetClientsAsync(new Modgud.Application.DTOs.OAuth.PaginationRequest { PageSize = 1 });
                    await oauthAdmin.GetScopesAsync();
                    await oauthAdmin.GetApisAsync(new Modgud.Application.DTOs.OAuth.PaginationRequest { PageSize = 1 });
                }
            }
        }
        catch (Exception ex)
        {
            // Warmup is best-effort. A failure here doesn't prevent boot —
            // the first user request would just pay the cold-start cost.
            Log.Warning(ex, "Marten LINQ warmup failed (non-fatal).");
        }

        if (configuredRealms.Count == 0)
            Log.Information("No realm exists yet; Modgud is waiting for first installation.");
    }

    // Headless command dispatch — run a recovery command instead of starting
    // Kestrel. Two ways in, both run AFTER the master/global bootstrap above;
    // realm-scoped commands additionally require an existing realm:
    //   1. CLI args:  dotnet Modgud.Api.dll recover <command> [args...]
    //                 → runs the command, returns its exit code (process exits).
    //   2. STARTUP_COMMAND env var (Portainer/Compose-friendly, no entrypoint
    //      override): the value is split into argv and run; the process then
    //      IDLES (no Kestrel, never exits) so a container restart-policy can't
    //      crash-loop it. The operator removes the variable and redeploys to
    //      resume normal web serving. Only consulted when no CLI command args
    //      are present. STARTUP_COMMAND is a raw env var (not a
    //      Cocoar.Configuration key), matching the Docker convention.
    var startupCommand = Environment.GetEnvironmentVariable("STARTUP_COMMAND");
    var hasCliCommand = args.Length > 0;
    var fromEnv = !hasCliCommand && !string.IsNullOrWhiteSpace(startupCommand);
    var cliArgs = hasCliCommand
        ? args
        : (fromEnv ? SplitCommandLine(startupCommand!) : []);

    if (cliArgs.Length > 0 && cliArgs[0].Equals("recover", StringComparison.OrdinalIgnoreCase))
    {
        var exitCode = await Modgud.Authentication.Api.Admin.RecoveryCli.RunAsync(
            app.Services, cliArgs[1..], conf, app.Environment);

        // This path never starts the hosted writer, so route the queued realm and
        // platform records synchronously before the process exits.
        await app.Services.GetRequiredService<Modgud.Infrastructure.Audit.SecurityAuditLog>()
            .FlushAsync(
                app.Services.GetRequiredService<Marten.IDocumentStore>(),
                app.Services.GetRequiredService<Modgud.Infrastructure.Persistence.Tenancy.IGlobalStore>());

        if (fromEnv)
        {
            Log.Information(
                "STARTUP_COMMAND finished (exit {ExitCode}). Idling — remove the STARTUP_COMMAND env var and redeploy to serve normally.",
                exitCode);
            await Task.Delay(Timeout.Infinite);
        }
        return exitCode;
    }

    app.Run(conf.AppUrl);
    return 0;
}
catch (Exception ex)
{
    // Logged here with the full exception; the rethrow reaches the
    // UnhandledException handler above, which exits with code 1 (under an init
    // process the runtime would report 134/SIGABRT instead - both restart the
    // container).
    Log.Fatal(ex, "Host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Quote-aware command-line splitter for STARTUP_COMMAND. Splits on whitespace
// but keeps "double-quoted segments" together so a multi-word arg (e.g. a realm
// display name) survives as one token. Deliberately minimal — not a full POSIX
// shell parser (no escape sequences, no single quotes) — which is enough for
// the recover CLI's argv.
static string[] SplitCommandLine(string commandLine)
{
    var tokens = new List<string>();
    var current = new System.Text.StringBuilder();
    var inQuotes = false;

    foreach (var ch in commandLine)
    {
        if (ch == '"')
        {
            inQuotes = !inQuotes;
        }
        else if (char.IsWhiteSpace(ch) && !inQuotes)
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        else
        {
            current.Append(ch);
        }
    }

    if (current.Length > 0)
        tokens.Add(current.ToString());

    return tokens.ToArray();
}
