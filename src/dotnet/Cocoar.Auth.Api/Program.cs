using Cocoar.Auth.Api.Configuration;
using Cocoar.Auth.Application;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Infrastructure;
using Cocoar.Auth.Infrastructure.Interfaces;
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
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// Configure Cocoar.Configuration with layered sources (using builder extension like finoxl)
builder.AddCocoarConfiguration(rule =>
[
    // Base configuration
    rule.For<DatabaseSettings>().FromFile("configs/database-settings.json").Required(),
    rule.For<AuthSettings>().FromFile("configs/auth-settings.json"),
    rule.For<CorsSettings>().FromFile("configs/cors-settings.json"),
    rule.For<SmtpSettings>().FromFile("configs/smtp-settings.json"),
    rule.For<WebAuthnSettings>().FromFile("configs/webauthn-settings.json"),

    // Environment-specific overrides (e.g., appsettings.Development.json)
    //rule.For<DatabaseSettings>().FromFile($"configs/database-settings.{builder.Environment.EnvironmentName}.json"),
    rule.For<AuthSettings>().FromFile($"configs/auth-settings.{builder.Environment.EnvironmentName}.json"),
    rule.For<CorsSettings>().FromFile($"configs/cors-settings.{builder.Environment.EnvironmentName}.json"),
    rule.For<SmtpSettings>().FromFile($"configs/smtp-settings.{builder.Environment.EnvironmentName}.json"),
    rule.For<WebAuthnSettings>().FromFile($"configs/webauthn-settings.{builder.Environment.EnvironmentName}.json"),

    // Environment variable overrides (highest priority)
    rule.For<DatabaseSettings>().FromEnvironment("DATABASE_"),
    rule.For<AuthSettings>().FromEnvironment("AUTH_"),
    rule.For<CorsSettings>().FromEnvironment("CORS_"),
    rule.For<SmtpSettings>().FromEnvironment("SMTP_"),
    rule.For<WebAuthnSettings>().FromEnvironment("WEBAUTHN_"),

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
	setup.Secrets().UseCertificatesFromFolder("configs/certificates").AllowPlaintext(),
]);

// Get configuration manager for bootstrap access
var configManager = builder.GetCocoarConfigManager();
var projectionSettings = configManager.GetConfig<ProjectionSettings>();
var dbSettings = configManager.GetConfig<DatabaseSettings>();

// Add services to the container
builder.Services.AddInfrastructure(projectionSettings.UseAsyncProjections);
builder.Services.AddIdentityWithMarten();
builder.Services.AddApplication();

// Register SMTP email sender (overrides mock from AddInfrastructure)
var smtpSettings = configManager.GetConfig<SmtpSettings>();
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

// Register Fido2 for WebAuthn
var webAuthnSettings = configManager.GetConfig<WebAuthnSettings>();
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

        // Return 401/403 for API instead of redirects
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new OptionalJsonConverterFactory());
        options.JsonSerializerOptions.Converters.Add(new ShortGuidJsonConverter());
        options.JsonSerializerOptions.TypeInfoResolver = new OptionalAwareTypeInfoResolver();
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS with deferred configuration
builder.Services.AddCors();

var app = builder.Build();

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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run("http://0.0.0.0:80");

// Make the implicit Program class public for integration tests
public partial class Program { }
