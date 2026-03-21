using System.Security.Claims;
using System.Threading.RateLimiting;
using Cocoar.Auth.Api.Configuration;
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
using Wolverine;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// Configure Cocoar.Configuration with layered sources (using builder extension like finoxl)
builder.AddCocoarConfiguration(c => c.UseConfiguration(rule =>
[
    // Base configuration
    rule.For<DatabaseSettings>().FromFile("configs/database-settings.json").Required(),
    rule.For<AuthSettings>().FromFile("configs/auth-settings.json"),
    rule.For<CorsSettings>().FromFile("configs/cors-settings.json"),
    rule.For<SmtpSettings>().FromFile("configs/smtp-settings.json"),
    rule.For<WebAuthnSettings>().FromFile("configs/webauthn-settings.json"),
    rule.For<OpenIddictSettings>().FromFile("configs/openiddict-settings.json"),
    rule.For<ServerSettings>().FromFile("configs/server-settings.json"),

    // Environment-specific overrides (e.g., appsettings.Development.json)
    //rule.For<DatabaseSettings>().FromFile($"configs/database-settings.{builder.Environment.EnvironmentName}.json"),
    rule.For<AuthSettings>().FromFile($"configs/auth-settings.{builder.Environment.EnvironmentName}.json"),
    rule.For<CorsSettings>().FromFile($"configs/cors-settings.{builder.Environment.EnvironmentName}.json"),
    rule.For<SmtpSettings>().FromFile($"configs/smtp-settings.{builder.Environment.EnvironmentName}.json"),
    rule.For<WebAuthnSettings>().FromFile($"configs/webauthn-settings.{builder.Environment.EnvironmentName}.json"),
    rule.For<OpenIddictSettings>().FromFile($"configs/openiddict-settings.{builder.Environment.EnvironmentName}.json"),

    // Environment variable overrides (highest priority)
    rule.For<DatabaseSettings>().FromEnvironment("DATABASE_"),
    rule.For<AuthSettings>().FromEnvironment("AUTH_"),
    rule.For<CorsSettings>().FromEnvironment("CORS_"),
    rule.For<SmtpSettings>().FromEnvironment("SMTP_"),
    rule.For<WebAuthnSettings>().FromEnvironment("WEBAUTHN_"),
    rule.For<OpenIddictSettings>().FromEnvironment("OPENIDDICT_"),
    rule.For<ServerSettings>().FromEnvironment("SERVER_"),

    // Static configuration (cannot be overridden by JSON files, but can be overridden in tests)
    // Use inline projections in development to avoid async daemon lock acquisition issues
    rule.For<ProjectionSettings>().FromStatic(_ => new ProjectionSettings { UseAsyncProjections = builder.Environment.IsProduction() })
], setup =>
[
    // Expose settings as singletons for DI
    setup.ConcreteType<DatabaseSettings>().AsSingleton(),
    setup.ConcreteType<AuthSettings>().AsSingleton(),
    setup.ConcreteType<CorsSettings>().AsSingleton(),
    setup.ConcreteType<ProjectionSettings>().AsSingleton(),
    setup.ConcreteType<SmtpSettings>().AsSingleton(),
    setup.ConcreteType<WebAuthnSettings>().AsSingleton(),

	setup.ConcreteType<DatabaseSettings>().ExposeAs<IDatabaseSettings>(),
	setup.ExposedType<IDatabaseSettings>().AsSingleton(),
	setup.ConcreteType<ProjectionSettings>().ExposeAs<IProjectionSettings>(),
	setup.ConcreteType<SmtpSettings>().ExposeAs<ISmtpSettings>(),
	setup.ExposedType<ISmtpSettings>().AsSingleton(),
	setup.ConcreteType<WebAuthnSettings>().ExposeAs<IWebAuthnSettings>(),
	setup.ExposedType<IWebAuthnSettings>().AsSingleton(),
	setup.ConcreteType<OpenIddictSettings>().AsSingleton(),
	setup.ConcreteType<ServerSettings>().AsSingleton(),
	setup.ConcreteType<OpenIddictSettings>().ExposeAs<IOpenIddictSettings>(),
	setup.ExposedType<IOpenIddictSettings>().AsSingleton(),
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

// Build full connection strings with embedded password for Marten multi-tenancy
// Config DB name is the base prefix: "cocoar_auth" → master: "cocoar_auth_master", system: "cocoar_auth_system"
var csBuilder = new NpgsqlConnectionStringBuilder(dbSettings.ConnectionString);
using (var pwd = dbSettings.Password.Open())
{
    csBuilder.Password = pwd.Value;
}
var baseDbName = csBuilder.Database ?? "cocoar_auth";
csBuilder.Database = baseDbName + "_system";
var systemRealmCs = csBuilder.ConnectionString;
csBuilder.Database = baseDbName + "_master";
var masterCs = csBuilder.ConnectionString;

// Register master connection string for realm provisioning
builder.Services.AddSingleton<IMasterConnectionString>(new MasterConnectionString(masterCs));

// Add services to the container
builder.Services.AddInfrastructure(projectionSettings.UseAsyncProjections, masterCs, systemRealmCs);
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

// Configure Wolverine
builder.Host.UseWolverine(opts =>
{
	// Discover handlers in the Application assembly
    opts.Discovery.IncludeAssembly(typeof(Cocoar.Auth.Application.DependencyInjection).Assembly);

    // Use local, in-memory queue (no external message transport needed)
    opts.Durability.Mode = DurabilityMode.Solo;
});

// Configure authentication cookies using options pattern
builder.Services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
    .Configure<AuthSettings>((options, authSettings) =>
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

        // Scope cookie path to realm to prevent cross-realm session leakage
        var originalOnSigningIn = options.Events.OnSigningIn;
        options.Events.OnSigningIn = context =>
        {
            var realmSlug = context.HttpContext.Items["RealmSlug"] as string ?? "system";

            // Add cocoar:realm claim for auditing/logging
            var identity = (ClaimsIdentity)context.Principal!.Identity!;
            identity.AddClaim(new Claim("cocoar:realm", realmSlug));

            if (realmSlug == "system")
            {
                // System admin cookie needs path "/" to reach all realm paths
                context.CookieOptions.Path = "/";
            }
            else
            {
                context.CookieOptions.Path = $"/{realmSlug}";
            }
            return originalOnSigningIn?.Invoke(context) ?? Task.CompletedTask;
        };

        // Match cookie path on sign-out so the browser actually deletes the cookie
        var originalOnSigningOut = options.Events.OnSigningOut;
        options.Events.OnSigningOut = context =>
        {
            var realmSlug = context.HttpContext.Items["RealmSlug"] as string ?? "system";
            context.CookieOptions.Path = realmSlug == "system" ? "/" : $"/{realmSlug}";
            return originalOnSigningOut?.Invoke(context) ?? Task.CompletedTask;
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

builder.Services.AddHealthChecks()
    .AddNpgSql(systemRealmCs, name: "postgresql");

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

    options.AddFixedWindowLimiter("general", opt =>
    {
        opt.PermitLimit = 60;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

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
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'";
    await next();
});

// Configure CORS policy using resolved configuration
var corsSettings = app.Services.GetRequiredService<CorsSettings>();
app.UseCors(policy =>
{
    if (corsSettings.AllowedOrigins.Length > 0)
    {
        policy.WithOrigins(corsSettings.AllowedOrigins);
    }

    if (corsSettings.AllowedMethods.Length > 0)
    {
        policy.WithMethods(corsSettings.AllowedMethods);
    }
    else
    {
        policy.AllowAnyMethod();
    }

    if (corsSettings.AllowedHeaders.Length > 0)
    {
        policy.WithHeaders(corsSettings.AllowedHeaders);
    }
    else
    {
        policy.AllowAnyHeader();
    }

    if (corsSettings.AllowCredentials)
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

//app.UseHttpsRedirection();

app.UseSerilogRequestLogging();

app.UseRateLimiter();

// Realm middleware: resolves tenant from first path segment (/{slug}/...)
// Must run BEFORE UseRouting so that PathBase is set before route matching occurs.
app.UseMiddleware<RealmMiddleware>();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Serve Vue SPA static files from wwwroot (CSS, JS, assets)
app.UseStaticFiles();

app.MapHealthChecks("/health");
app.MapControllers();

// SPA fallback: any unmatched route → index.html (Vue router handles it)
app.MapFallbackToFile("index.html");

// Ensure the master and system databases exist (auto-created on first start)
var masterDbName = baseDbName + "_master";
var systemDbName = baseDbName + "_system";
var bootstrapBuilder = new NpgsqlConnectionStringBuilder(masterCs) { Database = "postgres" };
await using (var bootstrapConn = new NpgsqlConnection(bootstrapBuilder.ConnectionString))
{
    await bootstrapConn.OpenAsync();
    foreach (var dbName in new[] { masterDbName, systemDbName })
    {
        await using var checkCmd = new NpgsqlCommand(
            $"SELECT 1 FROM pg_database WHERE datname = '{dbName}'", bootstrapConn);
        var exists = await checkCmd.ExecuteScalarAsync();
        if (exists is null)
        {
            await using var createCmd = new NpgsqlCommand(
                $"CREATE DATABASE \"{dbName}\"", bootstrapConn);
            await createCmd.ExecuteNonQueryAsync();
        }
    }
}

// Apply Marten schema changes (creates realms.mt_tenant_databases table, document schemas, etc.)
// Must happen before any Marten session usage since ApplyAllDatabaseChangesOnStartup runs as
// a hosted service which hasn't started yet at this point in Program.cs.
var store = app.Services.GetRequiredService<Marten.IDocumentStore>();
await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

// Seed system realm document (idempotent)
using (var realmScope = app.Services.CreateScope())
{
    var realmService = realmScope.ServiceProvider.GetRequiredService<IRealmProvisioningService>();
    await realmService.EnsureSystemRealmExistsAsync();
}

// Initialize the realm cache
var realmCache = app.Services.GetRequiredService<IRealmCache>();
await realmCache.InitializeAsync();

// Seed default OAuth scopes (openid, email, profile, roles, offline_access)
await app.Services.SeedOpenIddictScopesAsync();

// Seed built-in "Internal" login provider
await app.Services.SeedLoginProvidersAsync();

app.Run(serverSettings.AppUrl);

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
