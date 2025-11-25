using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Identity;
using Cocoar.Auth.Infrastructure.Persistence;
using Cocoar.Auth.Infrastructure.Services;
using JasperFx;
using Marten;
using Marten.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core.Migrations;

namespace Cocoar.Auth.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        // Configure Marten
        services.AddMarten(options =>
        {
            options.Connection(connectionString);
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // Configure System.Text.Json to handle private setters
            options.UseSystemTextJsonForSerialization(configure: o =>
            {
                o.PropertyNamingPolicy = null; // Use exact property names
                o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });

            // Configure ApplicationUser document
            options.Schema.For<ApplicationUser>()
                .Identity(x => x.Id)
                .Index(x => x.NormalizedUserName!, x => x.IsUnique = true)
                .Index(x => x.NormalizedEmail!);

            // Configure ApplicationRole document
            options.Schema.For<ApplicationRole>()
                .Identity(x => x.Id)
                .Index(x => x.NormalizedName, x => x.IsUnique = true);
        })
        .UseLightweightSessions();

        // Register repositories
        services.AddScoped<IUserRepository, MartenUserRepository>();
        services.AddScoped<IRoleRepository, MartenRoleRepository>();

        // Register email sender
        services.AddSingleton<MockEmailSender>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<MockEmailSender>());

        // Register authentication service
        services.AddScoped<IAuthenticationService, AspNetCoreAuthenticationService>();

        return services;
    }

    public static IdentityBuilder AddIdentityWithMarten(this IServiceCollection services)
    {
        return services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
        {
            // Password settings
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequiredLength = 8;
            options.Password.RequiredUniqueChars = 1;

            // Lockout settings
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;

            // User settings
            options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
            options.User.RequireUniqueEmail = false; // We handle this in the service layer

            // Sign-in settings
            options.SignIn.RequireConfirmedEmail = false; // Can be enabled later
            options.SignIn.RequireConfirmedPhoneNumber = false;
        })
        .AddUserStore<MartenUserStore>()
        .AddRoleStore<MartenRoleStore>()
        .AddDefaultTokenProviders();
    }
}
