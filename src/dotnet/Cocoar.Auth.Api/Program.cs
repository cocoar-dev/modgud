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
using Cocoar.Auth.Api.Features.Dev;
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
using Cocoar.Auth.Api.Features.Auth.OAuth;
using Cocoar.Auth.Authentication.Setup;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;
using Cocoar.Auth.Authentication.Identity.LoginProviders;
using Cocoar.Auth.Authentication.Identity.LoginProviders.Flavors;
using Cocoar.Auth.Api.Middleware;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;
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

            // Control-Plane (C14) — hostname list for the cross-realm
            // administration surface. Boot validation in this Program.cs
            // checks that every hostname maps to the Control-Plane realm.
            rule.For<ControlPlaneSettings>().FromFile("data/configuration.json").Select("ControlPlane"),
            rule.For<ControlPlaneSettings>().FromFile("data/configuration.local.json").Select("ControlPlane"),
            rule.For<ControlPlaneSettings>().FromEnvironment("ControlPlane"),
        ], setup =>
        [
            setup.ConcreteType<StartUpConfiguration>().AsSingleton(),
            setup.ConcreteType<EmailConfiguration>().AsSingleton(),
            setup.ConcreteType<MagicLinkConfiguration>().AsSingleton(),
            setup.ConcreteType<EmailOtpConfiguration>().AsSingleton(),
            setup.ConcreteType<AppSettings>().AsSingleton(),
            setup.ConcreteType<OpenIddictSettings>().AsSingleton(),
            setup.ConcreteType<ControlPlaneSettings>().AsSingleton(),
        ]));

    // Expose concrete config types as Authentication interfaces so Authentication
    // can inject them without depending on the Api project.
    builder.Services.AddSingleton<IAuthSettings>(sp => sp.GetRequiredService<AppSettings>());
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
        options.Cookie.SecurePolicy = builder.Environment.IsProduction()
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.None;
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
            // Always in Production. None in Dev — Vite proxy connects via HTTPS but
            // browser receives response on HTTP, so Secure cookies won't be set.
            options.Cookie.SecurePolicy = builder.Environment.IsProduction()
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.None;
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
            options.Cookie.SecurePolicy = builder.Environment.IsProduction()
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.None;
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
            options.Cookie.SecurePolicy = builder.Environment.IsProduction()
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.None;
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
    //   * /api/setup/* → IP. Setup is one-shot per deployment.
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

        options.AddPolicy("setup", context =>
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
    });

    builder.Services.AddHttpContextAccessor();

    // IPermissionService + IPrincipalEmailResolver + IPrincipalLookupService + IMembershipEvaluator
    // + IAutoMembershipRecalculator are all registered by AddCocoarAuthAuthorization
    // inside AddInfrastructure. Only keep app-specific wiring here.
    builder.Services.AddScoped<IAdminNotifier, AdminNotifier>();
    // Demo seed importer — reads data/demo-seed.json on first-time setup when
    // the operator opts in. The service is itself stateless; it scopes its
    // own session per call so re-runs (e.g. setup-completed → admin re-runs
    // with LoadDemoData=true) don't leak across invocations.
    // PROD-01: do NOT expose the demo seeder in Production. The seed file is
    // also publish-excluded (see csproj), but the in-process service is the
    // belt-and-suspenders gate — even if a build accidentally ships the JSON,
    // there is nothing to call it. The DemoSeedService dependency on
    // SetupEndpoints.create-admin is `IDemoSeedService? = null`, so the
    // endpoint silently falls through when the service isn't registered.
    if (!builder.Environment.IsProduction())
    {
        builder.Services.AddScoped<IDemoSeedService, DemoSeedService>();
    }

    // External auth (Phase 1–2: flavor registry + dynamic OIDC scheme registration)
    builder.Services.AddSingleton<ILoginProviderFlavor, EntraIdFlavor>();
    builder.Services.AddSingleton<ILoginProviderFlavor, GenericOidcFlavor>();
    builder.Services.AddSingleton<LoginProviderFlavorRegistry>();
    builder.Services.AddSingleton<LoginProviderSecretStore>();
    builder.Services.AddScoped<Cocoar.Auth.Application.Services.ILoginProviderRealmSeeder,
        Cocoar.Auth.Authentication.Setup.LoginProviderRealmSeeder>();
    builder.Services.AddSingleton<UserUpdateScriptRunner>();
    builder.Services.AddSingleton<DynamicOidcSchemeManager>();
    builder.Services.AddScoped<ExternalLoginProcessor>();
    builder.Services.AddHostedService<OidcSchemeBootstrap>();

    // SETUP-01 — first-run setup token (Production / non-Development only).
    builder.Services.AddSingleton<Cocoar.Auth.Authentication.Configuration.ISetupTokenService,
                                   Cocoar.Auth.Api.Features.Setup.SetupTokenService>();
    builder.Services.AddHostedService<Cocoar.Auth.Api.Features.Setup.SetupTokenBootstrap>();

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

    // OpenIddict OAuth 2.0 / OIDC server — uses our custom Marten stores. Settings are
    // captured at config time so signing certs / lifetimes can be pinned before the
    // host is built. Per-realm issuer is applied at request time via RealmIssuerHandler.
    var openIddictSettings = configManager.GetRequiredConfig<OpenIddictSettings>();

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
                "OpenIddict__DevelopmentMode=false and provide a real signing cert.");

        if (string.IsNullOrWhiteSpace(openIddictSettings.SigningCertificatePath))
            throw new InvalidOperationException(
                "OpenIddict.SigningCertificatePath is required when DevelopmentMode=false. " +
                "Provide the path to a PFX/PEM file holding the production signing key.");

        if (string.IsNullOrWhiteSpace(openIddictSettings.Issuer) ||
            openIddictSettings.Issuer.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            openIddictSettings.Issuer.Contains("127.0.0.1"))
            throw new InvalidOperationException(
                $"OpenIddict.Issuer ('{openIddictSettings.Issuer}') is invalid for Production. " +
                "Set it to the public HTTPS URL of the IdP (e.g. https://auth.cocoar.dev).");

        if (openIddictSettings.Issuer.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"OpenIddict.Issuer ('{openIddictSettings.Issuer}') must use HTTPS in Production.");
    }

    builder.Services.AddOpenIddictWithMarten(openIddictSettings);

    // Migration services for legacy Cocoar.Auth data have been removed in the
    // IdP-only baseline — no historical documents to upgrade to event streams.

    // Wolverine CQRS + Marten projection side effects
    builder.Host.UseWolverine(opts =>
    {
        opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
        opts.Discovery.IncludeAssembly(typeof(Cocoar.Auth.Authentication.Api.Admin.RecoveryCli).Assembly);
        opts.Discovery.IncludeAssembly(typeof(Cocoar.Auth.Authorization.Commands.CreateGroupCommand).Assembly);
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Auto;

        // Auto-register Event Forwarding subscriptions for all ReferenceSyncHandler<TEvent> implementations
        ReferenceSyncRegistration.RegisterAll(opts, typeof(Program).Assembly);
    });

    // Auth log: Serilog sink → Channel → BackgroundService → Marten (7-day retention)
    var authLogSink = new AuthLogSink();
    builder.Services.AddSingleton(authLogSink);
    builder.Services.AddHostedService<AuthLogPersistenceService>();

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
    // (see /connect/* + /api/setup/* + /api/account/forgot-password +
    // /api/account/magic-link below). Endpoints without an explicit
    // policy are not rate-limited at the app layer.
    app.UseRateLimiter();


    // OpenIddict OAuth/OIDC endpoints (/connect/authorize, /token, /userinfo, /logout, /consent).
    // OpenIddict's middleware is registered as part of UseOpenIddict... hooks called by
    // ASP.NET Core during AddOpenIddict. The discovery + JWKS endpoints are auto-mapped
    // by OpenIddict; only the passthrough endpoints (authorize/token/userinfo/...) need
    // explicit minimal-API handlers.
    app.MapAuthorizationEndpoints();
    app.MapConsentEndpoints();

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

    // Account & Setup Endpoints (have additional strict "auth" rate limit)
    app.MapAccountEndpoints("api");
    app.MapProfileEndpoints("api");
    app.MapMfaEndpoints("api");
    app.MapEmailOtpEndpoints("api");
    app.MapPasskeyEndpoints("api");
    app.MapMagicLinkEndpoints("api");
    app.MapPasswordResetEndpoints("api");
    app.MapSetupEndpoints("api");
    app.MapSessionEndpoints("api");
    app.MapGdprEndpoints("api");
    app.MapExternalAuthEndpoints("api");
    app.MapProfileLinkEndpoints("api");
    app.MapLoginProvidersEndpoints("api");
    app.MapUserUpdateScriptTestEndpoint("api");

    // Marten Endpoints
    app.MapUsersEndpoints("api");
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
            await using var createCmd = new NpgsqlCommand(
                $"CREATE DATABASE {quotedName}", bootstrapConn);
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

        // C14 boot-validation: every configured Control-Plane hostname must
        // resolve to a realm flagged IsControlPlane=true. A typo in
        // ControlPlane__Hostnames in Production would otherwise quietly
        // expose realm CRUD on a tenant host. Dev skips the check and
        // implicitly trusts the system realm's own Domains list, so a
        // fresh checkout boots without ENV setup.
        if (!app.Environment.IsDevelopment())
        {
            var cpSettings = realmScope.ServiceProvider.GetRequiredService<ControlPlaneSettings>();
            if (cpSettings.Hostnames.Length == 0)
            {
                throw new InvalidOperationException(
                    "ControlPlane__Hostnames must be set in non-Development environments — " +
                    "the deployment needs to know which hostnames serve the cross-realm admin surface. " +
                    "Set e.g. ControlPlane__Hostnames=auth.example.com");
            }

            foreach (var host in cpSettings.Hostnames)
            {
                var resolved = await realmCache.ResolveDomainAsync(host);
                if (resolved is null)
                {
                    throw new InvalidOperationException(
                        $"ControlPlane hostname '{host}' does not resolve to any active realm. " +
                        $"Add '{host}' to the Domains of the Control-Plane realm or remove it from ControlPlane__Hostnames.");
                }
                if (!resolved.IsControlPlane)
                {
                    throw new InvalidOperationException(
                        $"ControlPlane hostname '{host}' resolves to realm '{resolved.Slug}', " +
                        $"which is NOT flagged IsControlPlane. Misconfigured hostname lists would " +
                        $"otherwise expose cross-realm admin endpoints on a tenant host.");
                }
            }

            Log.Information(
                "Control-Plane gate validated: {Count} hostname(s) → realm '{Slug}' (IsControlPlane=true)",
                cpSettings.Hostnames.Length,
                (await realmCache.ResolveDomainAsync(cpSettings.Hostnames[0]))?.Slug);
        }
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
