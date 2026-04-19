using System.Security.Claims;
using System.Threading.RateLimiting;
using JasperFx;
using Cocoar.Auth.Api.Configuration;
using Cocoar.Auth.Api.Extensions;
using Cocoar.Auth.Api.Middleware;
using Cocoar.Auth.Application;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Infrastructure;
using Cocoar.Auth.Infrastructure.Interfaces;
using Cocoar.Auth.Infrastructure.OpenIddict;
using Cocoar.Auth.Infrastructure.Repositories;
using Cocoar.Auth.Infrastructure.Services;
using Cocoar.Configuration.AspNetCore;
using Cocoar.Configuration.DI;
using Cocoar.Configuration.DI.Extensions;
using Cocoar.Configuration.Fluent;
using Cocoar.Configuration.Providers;
using Cocoar.Configuration.Secrets;
using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;
using Fido2NetLib;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Serilog;
using Cocoar.Auth.Api.Hubs;
using Cocoar.Configuration.Reactive;
using Cocoar.SignalARRR.Server.ExtensionMethods;
using JasperFx.Events.Daemon;
using Marten;
using Wolverine;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog via DI (NOT static bootstrap logger).
// Using AddSerilog avoids ReloadableLogger.Freeze() which throws
// "The logger is already frozen" when multiple WebApplicationFactory
// instances start in parallel during test execution.
builder.Services.AddSerilog(configuration =>
{
    configuration
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console();

    // Suppress verbose Marten/Wolverine logging in tests
    if (builder.Environment.IsEnvironment("Testing"))
    {
        configuration.MinimumLevel.Warning();
    }
});

// Configure Cocoar.Configuration with layered sources
var env = builder.Environment.EnvironmentName;
builder.AddCocoarConfiguration(c => c.UseConfiguration(rule =>
[
    rule.For<DatabaseSettings>().Layered("database-settings", "DATABASE_", env).Required(),
    rule.For<AuthSettings>().Layered("auth-settings", "AUTH_", env),
    rule.For<CorsSettings>().Layered("cors-settings", "CORS_", env),
    rule.For<SmtpSettings>().Layered("smtp-settings", "SMTP_", env),
    rule.For<WebAuthnSettings>().Layered("webauthn-settings", "WEBAUTHN_", env),
    rule.For<OpenIddictSettings>().Layered("openiddict-settings", "OPENIDDICT_", env),
    rule.For<ServerSettings>().Layered("server-settings", "SERVER_", env),
    rule.For<ProjectionSettings>().FromStatic(_ => new ProjectionSettings { UseAsyncProjections = builder.Environment.IsProduction() })
], setup =>
[
    setup.ConcreteType<DatabaseSettings>().ExposeAs<IDatabaseSettings>(),
    setup.ConcreteType<ProjectionSettings>().ExposeAs<IProjectionSettings>(),
    setup.ConcreteType<SmtpSettings>().ExposeAs<ISmtpSettings>(),
    setup.ConcreteType<WebAuthnSettings>().ExposeAs<IWebAuthnSettings>(),
    setup.ConcreteType<OpenIddictSettings>().ExposeAs<IOpenIddictSettings>(),
]).UseSecretsSetup(secrets => secrets.UseCertificatesFromFolder("configs/certificates").AllowPlaintext()));

// Get configuration manager for bootstrap access
var configManager = builder.GetCocoarConfigManager();
var projectionSettings = configManager.GetConfig<ProjectionSettings>()!;
var dbSettings = configManager.GetConfig<DatabaseSettings>()!;
var serverSettings = configManager.GetConfig<ServerSettings>()!;

// Configure Kestrel with TLS certificate if HTTPS is requested
var needsSsl = serverSettings.AppUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
var effectiveCertPath = serverSettings.CertPath;

// If HTTPS but no CertPath configured, use a default path
if (needsSsl && string.IsNullOrWhiteSpace(effectiveCertPath))
{
    effectiveCertPath = "certs/cocoar-auth.pfx";
}

if (!string.IsNullOrWhiteSpace(effectiveCertPath))
{
    var certPath = Path.GetFullPath(effectiveCertPath);
    // Normalize empty password to null (passwordless PFX)
    var certPassword = string.IsNullOrEmpty(serverSettings.CertPassword) ? null : serverSettings.CertPassword;

    // Auto-generate self-signed certificate if the file doesn't exist
    if (!File.Exists(certPath))
    {
        Log.Information("Certificate not found at {CertPath} — generating self-signed certificate", certPath);
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=Cocoar Auth (Self-Signed)", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        var pfxBytes = selfSigned.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, certPassword);
        var certDir = Path.GetDirectoryName(certPath);
        if (!string.IsNullOrEmpty(certDir)) Directory.CreateDirectory(certDir);
        File.WriteAllBytes(certPath, pfxBytes);
        Log.Information("Self-signed certificate saved to {CertPath}", certPath);
    }

    var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader
        .LoadPkcs12FromFile(certPath, certPassword);
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ConfigureHttpsDefaults(httpsOptions =>
        {
            httpsOptions.ServerCertificate = cert;
        });
    });
}

// Build connection string with embedded password for Marten multi-tenancy.
// Single DB serves as both tenant registry (master) AND system tenant data.
// This enables IntegrateWithWolverine (needs a valid default DB for outbox tables).
var csBuilder = new NpgsqlConnectionStringBuilder(dbSettings.ConnectionString);
using (var pwd = dbSettings.Password.Open())
{
    csBuilder.Password = pwd.Value;
}
var mainCs = csBuilder.ConnectionString;
var baseDbName = csBuilder.Database ?? "cocoar_auth";

// Register main connection string for realm provisioning
builder.Services.AddSingleton<IMasterConnectionString>(new MasterConnectionString(mainCs));

// Add services to the container
builder.Services.AddGlobalStore(mainCs);
builder.Services.AddInfrastructure();
builder.Services.AddIdentityWithMarten();
builder.Services.AddApplication();

// Register realm services
builder.Services.AddSingleton<IRealmCache, RealmCache>();
builder.Services.AddScoped<IRealmProvisioningService, RealmProvisioningService>();

// Add OpenIddict with Marten for OAuth 2.0 / OpenID Connect
var openIddictSettings = configManager.GetConfig<OpenIddictSettings>()!;
builder.Services.AddOpenIddictWithMarten(openIddictSettings);
builder.Services.ConfigureOpenIddictServerOptions<OpenIddictSettings>();

// Register OAuth admin service
builder.Services.AddScoped<OAuthAdminService>();

// Register SMTP email sender (overrides mock from AddInfrastructure)
// Skip in Testing environment to use MockEmailSender for tests
if (!builder.Environment.IsEnvironment("Testing"))
{
    var smtpSettings = configManager.GetConfig<SmtpSettings>()!;
    builder.Services.AddSingleton(new SmtpEmailSenderOptions
    {
        Host = smtpSettings.Host,
        Port = smtpSettings.Port,
        UseSsl = smtpSettings.UseSsl,
        Username = smtpSettings.Username,
        Password = smtpSettings.Password,
        FromAddress = smtpSettings.FromAddress,
        FromName = smtpSettings.FromName
    });
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
}

// Register Fido2 for WebAuthn
var webAuthnSettings = configManager.GetConfig<WebAuthnSettings>()!;
var fido2Config = new Fido2Configuration
{
    ServerDomain = webAuthnSettings.RelyingPartyId,
    ServerName = webAuthnSettings.RelyingPartyName,
    Origins = webAuthnSettings.Origins.ToHashSet(),
    TimestampDriftTolerance = 300000 // 5 minutes
};
builder.Services.AddSingleton(fido2Config);
builder.Services.AddSingleton<IFido2, Fido2>();

// Configure Wolverine + Marten (integrated for transactional outbox support)
builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Cocoar.Auth.Application.DependencyInjection).Assembly);
    opts.Durability.Mode = DurabilityMode.Solo;

    // Use pre-generated handler code when available (Auto = try pre-built, fallback to dynamic).
    // Generate with: dotnet run -- codegen write
    opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Auto;

    // Auto-register reference-sync subscriptions for every ReferenceSyncHandler<TEvent>
    // discovered in this assembly. Each handler runs on the "reference-sync" local
    // durable queue with at-least-once delivery semantics.
    Cocoar.Auth.Api.Authorization.ReferenceSyncRegistration.RegisterAll(opts, typeof(Program).Assembly);

    // Marten — configured inside UseWolverine for IntegrateWithWolverine compatibility.
    // No RegisterDatabase needed — tenants registered dynamically via AddDatabaseRecordAsync.
    // Realm documents stored in IGlobalStore (non-tenanted), not here.
    var martenBuilder = opts.Services.AddMarten(
            Cocoar.Auth.Infrastructure.DependencyInjection.ConfigureMartenOptions(mainCs, projectionSettings.UseAsyncProjections))
        .IntegrateWithWolverine(x =>
        {
            x.MainDatabaseConnectionString = mainCs;
        })
        .ApplyAllDatabaseChangesOnStartup();

    if (projectionSettings.UseAsyncProjections)
    {
        martenBuilder.AddAsyncDaemon(DaemonMode.HotCold);
    }

    // Tenant-aware session registrations — MUST be AFTER AddMarten().IntegrateWithWolverine()
    // so our registrations win over Marten/Wolverine defaults (last-wins DI).
    // CRITICAL: Use DirtyTrackedSession, NOT LightweightSession — dirty tracking
    // auto-detects mutations on loaded documents (no explicit Store() needed),
    // enables the PendingEvents pattern on entities, and ensures inline projections
    // work correctly with the identity map.
    opts.Services.AddScoped<IDocumentSession>(sp =>
    {
        var store = sp.GetRequiredService<IDocumentStore>();
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        var tenantId = accessor.HttpContext?.Items["TenantId"] as string ?? "system";
        return store.DirtyTrackedSession(tenantId);
    });

    opts.Services.AddScoped<IQuerySession>(sp =>
    {
        var store = sp.GetRequiredService<IDocumentStore>();
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        var tenantId = accessor.HttpContext?.Items["TenantId"] as string ?? "system";
        return store.QuerySession(tenantId);
    });
});

// Configure authentication cookies
var authSettings = configManager.GetConfig<AuthSettings>()!;
builder.Services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
    .Configure(options =>
    {
        options.Cookie.HttpOnly = authSettings.Cookie.HttpOnly;
        options.Cookie.SecurePolicy = authSettings.Cookie.SecurePolicy switch
        {
            "Always" => CookieSecurePolicy.Always,
            "None" => CookieSecurePolicy.None,
            _ => CookieSecurePolicy.SameAsRequest
        };
        options.Cookie.SameSite = authSettings.Cookie.SameSite switch
        {
            "Strict" => SameSiteMode.Strict,
            "None" => SameSiteMode.None,
            _ => SameSiteMode.Lax
        };
        options.ExpireTimeSpan = TimeSpan.FromDays(authSettings.SessionExpirationDays);
        options.SlidingExpiration = authSettings.SlidingExpiration;
        options.LoginPath = "/api/auth/login";
        options.LogoutPath = "/api/auth/logout";
        options.AccessDeniedPath = "/api/auth/access-denied";

        // Return 401/403 for API instead of redirects, except for OAuth flows
        options.Events.OnRedirectToLogin = context =>
        {
            // For OAuth authorization endpoint, redirect to login page
            if (context.Request.Path.StartsWithSegments("/connect"))
            {
                // Let the default redirect behavior happen for OAuth flows
                // The frontend login page will handle returning to the authorize endpoint
                return Task.CompletedTask;
            }

            // For API calls, return 401
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };

        // Add realm claim for auditing (cookie isolation is automatic per domain)
        var originalOnSigningIn = options.Events.OnSigningIn;
        options.Events.OnSigningIn = context =>
        {
            var realmSlug = context.HttpContext.Items["RealmSlug"] as string ?? "system";
            var identity = (ClaimsIdentity)context.Principal!.Identity!;
            identity.AddClaim(new Claim("cocoar:realm", realmSlug));
            return originalOnSigningIn?.Invoke(context) ?? Task.CompletedTask;
        };
    });

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new OptionalJsonConverterFactory());
        options.JsonSerializerOptions.Converters.Add(new ShortGuidJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.JsonSerializerOptions.TypeInfoResolver = new OptionalAwareTypeInfoResolver();
    });

// Anti-forgery (CSRF) — belt-and-suspenders protection alongside SameSite cookies
builder.Services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");

builder.Services.AddHealthChecks()
    .AddNpgSql(mainCs, name: "postgresql");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("auth-strict", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("2fa-strict", opt =>
    {
        opt.PermitLimit = 3;
        opt.Window = TimeSpan.FromMinutes(5);
        opt.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("password-reset", opt =>
    {
        opt.PermitLimit = 3;
        opt.Window = TimeSpan.FromHours(1);
        opt.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("general", opt =>
    {
        opt.PermitLimit = 60;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Add SignalR + SignalARRR for real-time admin notifications
builder.Services.AddSignalR();
builder.Services.AddSignalARRR(options =>
	options.AddServerMethodsFrom(typeof(AdminHub).Assembly));
builder.Services.AddScoped<IAdminHubNotifier, AdminHubNotifier>();

// Add CORS with deferred configuration
builder.Services.AddCors();

var app = builder.Build();

// Security headers
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "0";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

// Configure CORS policy using resolved configuration
var corsSettings = app.Services.GetRequiredService<IReactiveConfig<CorsSettings>>();
app.UseCors(policy =>
{
    if (corsSettings.CurrentValue.AllowedOrigins.Length > 0)
    {
        policy.WithOrigins(corsSettings.CurrentValue.AllowedOrigins);
    }

    if (corsSettings.CurrentValue.AllowedMethods.Length > 0)
    {
        policy.WithMethods(corsSettings.CurrentValue.AllowedMethods);
    }
    else
    {
        policy.AllowAnyMethod();
    }

    if (corsSettings.CurrentValue.AllowedHeaders.Length > 0)
    {
        policy.WithHeaders(corsSettings.CurrentValue.AllowedHeaders);
    }
    else
    {
        policy.AllowAnyHeader();
    }

    if (corsSettings.CurrentValue.AllowCredentials)
    {
        policy.AllowCredentials();
    }
});

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseSerilogRequestLogging();

app.UseRateLimiter();

// Serve Vue SPA static files from wwwroot BEFORE realm middleware
// so that /assets/*.js, /assets/*.css are served directly without realm resolution.
app.UseStaticFiles();

// Realm middleware: resolves tenant from Host header (domain-based routing).
// Must run BEFORE UseRouting so tenant context is available for all downstream middleware.
app.UseMiddleware<RealmMiddleware>();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapControllers();
app.MapHARRRController<AdminHub>("/admin-hub");

// SPA fallback: any unmatched route → index.html (Vue router handles it)
app.MapFallbackToFile("index.html");

// Ensure the main database exists (auto-created on first start)
// Single DB serves as both tenant registry and system tenant data.
var bootstrapBuilder = new NpgsqlConnectionStringBuilder(mainCs) { Database = "postgres" };
await using (var bootstrapConn = new NpgsqlConnection(bootstrapBuilder.ConnectionString))
{
    await bootstrapConn.OpenAsync();
    await using var checkCmd = new NpgsqlCommand(
        "SELECT 1 FROM pg_database WHERE datname = @dbName", bootstrapConn);
    checkCmd.Parameters.AddWithValue("@dbName", baseDbName);
    var exists = await checkCmd.ExecuteScalarAsync();
    if (exists is null)
    {
        var quotedName = "\"" + baseDbName.Replace("\"", "\"\"") + "\"";
        await using var createCmd = new NpgsqlCommand(
            $"CREATE DATABASE {quotedName}", bootstrapConn);
        await createCmd.ExecuteNonQueryAsync();
    }
}

// Apply Marten schema changes (creates realms.mt_tenant_databases table, document schemas, etc.)
var store = app.Services.GetRequiredService<Marten.IDocumentStore>();
await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

// Register "system" tenant in Marten's tenancy at runtime (same DB as main connection).
// This is required because MasterTableTenancy doesn't support a default tenant —
// all tenants must be explicitly registered before sessions can be opened.
var tenancy = (Marten.Storage.MasterTableTenancy)store.Options.Tenancy;
await tenancy.AddDatabaseRecordAsync("system", mainCs);

// Seed system realm document in IGlobalStore (idempotent)
using (var realmScope = app.Services.CreateScope())
{
    var realmService = realmScope.ServiceProvider.GetRequiredService<IRealmProvisioningService>();
    await realmService.EnsureSystemRealmExistsAsync();
}

// Initialize the realm cache
var realmCache = app.Services.GetRequiredService<IRealmCache>();
await realmCache.InitializeAsync();

// Initialize ABAC resource registry
Cocoar.Auth.Domain.Authorization.ResourceRegistry.Initialize();

// Seed default OAuth scopes (openid, email, profile, roles, offline_access)
await app.Services.SeedOpenIddictScopesAsync();

// Seed built-in "Internal" login provider
await app.Services.SeedLoginProvidersAsync();

// Use RunJasperFxCommands to support Wolverine CLI commands (e.g., `dotnet run -- codegen write`
// to pre-generate handler code and eliminate runtime Roslyn compilation).
app.Urls.Add(serverSettings.AppUrl);
await app.RunJasperFxCommands(args);

// Make the implicit Program class public for integration tests
public partial class Program { }

/// <summary>
/// Simple implementation of IMasterConnectionString for DI registration.
/// </summary>
internal sealed class MasterConnectionString : IMasterConnectionString
{
    public string Value { get; }
    public MasterConnectionString(string value) => Value = value;
}
