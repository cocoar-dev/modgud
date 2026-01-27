using Cocoar.Auth.Api.Configuration;
using Cocoar.Auth.Application;
using Cocoar.Auth.Infrastructure;
using Cocoar.Configuration.AspNetCore;
using Cocoar.Configuration.DI;
using Cocoar.Configuration.DI.Extensions;
using Cocoar.Configuration.Providers;
using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// Configure Cocoar.Configuration with layered sources (using builder extension like finoxl)
builder.AddCocoarConfiguration(rule =>
[
    // Base configuration
    rule.For<DatabaseSettings>().FromFile("appsettings.json").Select("Database").Required(),
    rule.For<AuthSettings>().FromFile("appsettings.json").Select("Auth"),
    rule.For<CorsSettings>().FromFile("appsettings.json").Select("Cors"),

    // Environment-specific overrides (e.g., appsettings.Development.json)
    rule.For<DatabaseSettings>().FromFile($"appsettings.{builder.Environment.EnvironmentName}.json").Select("Database"),
    rule.For<AuthSettings>().FromFile($"appsettings.{builder.Environment.EnvironmentName}.json").Select("Auth"),
    rule.For<CorsSettings>().FromFile($"appsettings.{builder.Environment.EnvironmentName}.json").Select("Cors"),

    // Environment variable overrides (highest priority)
    rule.For<DatabaseSettings>().FromEnvironment("DATABASE_"),
    rule.For<AuthSettings>().FromEnvironment("AUTH_"),
    rule.For<CorsSettings>().FromEnvironment("CORS_"),

    // Static configuration (cannot be overridden by JSON files, but can be overridden in tests)
    rule.For<ProjectionSettings>().FromStatic(_ => new ProjectionSettings { UseAsyncProjections = true })
], setup =>
[
    // Expose settings as singletons for DI
    setup.ConcreteType<DatabaseSettings>().AsSingleton(),
    setup.ConcreteType<AuthSettings>().AsSingleton(),
    setup.ConcreteType<CorsSettings>().AsSingleton(),
    setup.ConcreteType<ProjectionSettings>().AsSingleton()
]);

// Get configuration manager for bootstrap access
var configManager = builder.GetCocoarConfigManager();
var dbSettings = configManager.GetRequiredConfig<DatabaseSettings>();
var projectionSettings = configManager.GetRequiredConfig<ProjectionSettings>();

// Add services to the container
builder.Services.AddInfrastructure(dbSettings.ConnectionString, projectionSettings.UseAsyncProjections);
builder.Services.AddIdentityWithMarten();
builder.Services.AddApplication();

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

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Make the implicit Program class public for integration tests
public partial class Program { }
