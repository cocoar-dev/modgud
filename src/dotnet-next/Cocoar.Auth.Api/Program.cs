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
using Cocoar.Auth.Authentication.AuthLog;
using Cocoar.Auth.Authentication.Api.Admin;
using Cocoar.Auth.Authentication.Api.Admin.IdentityProviders;
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
using Cocoar.Auth.Authentication.Setup;
using Cocoar.Auth.Authentication.Identity;
using Cocoar.Auth.Authentication.Identity.ExternalAuth;
using Cocoar.Auth.Authentication.Identity.ExternalAuth.Flavors;
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
        ], setup =>
        [
            setup.ConcreteType<StartUpConfiguration>().AsSingleton(),
            setup.ConcreteType<EmailConfiguration>().AsSingleton(),
            setup.ConcreteType<MagicLinkConfiguration>().AsSingleton(),
            setup.ConcreteType<EmailOtpConfiguration>().AsSingleton(),
            setup.ConcreteType<AppSettings>().AsSingleton(),
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

    // Trust reverse proxy headers (Sophos XG terminates HTTPS)
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                                 | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
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

    builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
        .AddCookie(IdentityConstants.ApplicationScheme, options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Always in Production. None in Dev — Vite proxy connects via HTTPS but
            // browser receives response on HTTP, so Secure cookies won't be set.
            options.Cookie.SecurePolicy = builder.Environment.IsProduction()
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.None;
            options.Cookie.Name = "Cocoar.Auth.Auth";
            options.ExpireTimeSpan = TimeSpan.FromDays(30); // Max lifetime for persistent (RememberMe) cookies
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = 403;
                return Task.CompletedTask;
            };
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
            options.RequireHttpsMetadata = false;
        });
    builder.Services.AddAuthorization();

    // NOTE: No application-level rate limiting. Defense layers:
    //   - Account Lockout: 5 failed logins per user → 5 min lock (implemented in Identity)
    //   - 2FA: planned — makes stolen passwords worthless
    //   - Sophos XG / Reverse Proxy: DDoS protection at infrastructure level
    // IP-based rate limiting is unreliable in corporate environments (NAT = shared IP).

    // Needed by AccessPolicyEngine so session-only external claims from the
    // active OIDC login are visible to access scripts as user.externalClaims.*
    builder.Services.AddHttpContextAccessor();

    // IPermissionService + IPrincipalEmailResolver + IPrincipalLookupService + IMembershipEvaluator
    // + IAccessPolicyEngine + IAutoMembershipRecalculator are all registered by
    // AddCocoarAuthAuthorization inside AddInfrastructure. Only keep app-specific wiring here.
    builder.Services.AddScoped<IAdminNotifier, AdminNotifier>();
    // IDemoSeedService is intentionally NOT registered in the IdP-only baseline.
    // SetupEndpoints injects it as `IDemoSeedService?` and skips seeding when absent.
    // Adopters that ship app-specific demo data register their own implementation.

    // External auth (Phase 1–2: flavor registry + dynamic OIDC scheme registration)
    builder.Services.AddSingleton<IIdentityProviderFlavor, EntraIdFlavor>();
    builder.Services.AddSingleton<IIdentityProviderFlavor, GenericOidcFlavor>();
    builder.Services.AddSingleton<FlavorRegistry>();
    builder.Services.AddSingleton<IdpSecretStore>();
    builder.Services.AddSingleton<UserUpdateScriptRunner>();
    builder.Services.AddSingleton<DynamicOidcSchemeManager>();
    builder.Services.AddScoped<ExternalLoginProcessor>();
    builder.Services.AddHostedService<OidcSchemeBootstrap>();

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

        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddSingleton<InMemoryEmailService>(sp =>
                new InMemoryEmailService(sp.GetRequiredService<ILogger<InMemoryEmailService>>(), emailService));
            builder.Services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<InMemoryEmailService>());
        }
        else
        {
            builder.Services.AddSingleton<IEmailService>(emailService);
        }
    
    builder.Services.AddScoped<IEmailOtpService, EmailOtpService>();

    // Infrastructure (Marten + repositories + query services + event dispatcher)
    // Authentication Marten setup (documents + events + projections) is wired via
    // UseCocoarAuthAuthentication() so Infrastructure stays unaware of Authentication.
    builder.Services.AddInfrastructure(conf.DbSettings.ConnectionString,
        options => options.UseCocoarAuthAuthentication());

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

    // Enable OpenAPI endpoint (not in production)
    if (!app.Environment.IsProduction())
    {
        app.MapOpenApi();
    }

    app.AddLogging();


    app.UseRouting();

    app.UseSession();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<Cocoar.Auth.Authentication.Api.Account.TwoFactorEnforcementMiddleware>();


    app.MapStatusEndpoints();
    app.MapAuthLogEndpoints("api");
    app.MapAppSettingsEndpoints("api");
    app.MapProjectionEndpoints("api");
    app.MapAdminMagicLinkEndpoints("api");
    app.MapAdminGraceEndpoints("api");
    app.MapAdminChangeRequestEndpoints("api");

    // Account & Setup Endpoints (have additional strict "auth" rate limit)
    app.MapAccountEndpoints("api");
    app.MapProfileEndpoints("api");
    app.MapMfaEndpoints("api");
    app.MapEmailOtpEndpoints("api");
    app.MapPasskeyEndpoints("api");
    app.MapMagicLinkEndpoints("api");
    app.MapPasswordResetEndpoints("api");
    app.MapSetupEndpoints("api");
    app.MapExternalAuthEndpoints("api");
    app.MapProfileLinkEndpoints("api");
    app.MapIdpConfigEndpoints("api");
    app.MapUserUpdateScriptTestEndpoint("api");

    // Dev-only endpoints (email inspection, MFA reset, etc.)
    // Available in Development environment (needed for E2E tests in Docker)
    if (app.Environment.IsDevelopment())
    {
        app.MapDevEndpoints("api");
    }

    // Marten Endpoints
    app.MapUsersEndpoints("api");
    app.MapPrincipalEndpoints("api");
    app.MapRolesEndpoints("api");
    app.MapGroupEndpoints("api");

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
