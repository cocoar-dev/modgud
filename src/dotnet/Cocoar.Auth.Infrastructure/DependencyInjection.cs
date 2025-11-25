using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Domain.Aggregates;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Infrastructure.Identity;
using Cocoar.Auth.Infrastructure.Persistence;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Cocoar.Auth.Infrastructure.Persistence.Repositories;
using Cocoar.Auth.Infrastructure.Services;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
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

            // ═══════════════════════════════════════════════════════════════
            // DOCUMENT STORAGE (Non-Event-Sourced)
            // ═══════════════════════════════════════════════════════════════

            // Configure ApplicationUser document (legacy, will migrate to event sourcing)
            options.Schema.For<ApplicationUser>()
                .Identity(x => x.Id)
                .Index(x => x.NormalizedUserName!, x => x.IsUnique = true)
                .Index(x => x.NormalizedEmail!);

            // Configure ApplicationRole document
            options.Schema.For<ApplicationRole>()
                .Identity(x => x.Id)
                .Index(x => x.NormalizedName, x => x.IsUnique = true);

            // Configure UserSecurityData document (security-sensitive data, not event-sourced)
            options.Schema.For<UserSecurityData>()
                .Identity(x => x.Id);

            // ═══════════════════════════════════════════════════════════════
            // EVENT SOURCING CONFIGURATION
            // ═══════════════════════════════════════════════════════════════

            // Register user events for the event store
            options.Events.AddEventType<UserCreated>();
            options.Events.AddEventType<UserNameChanged>();
            options.Events.AddEventType<UserEmailChanged>();
            options.Events.AddEventType<UserPhoneNumberChanged>();
            options.Events.AddEventType<UserProfileNameChanged>();
            options.Events.AddEventType<UserActivated>();
            options.Events.AddEventType<UserDeactivated>();
            options.Events.AddEventType<UserDeleted>();
            options.Events.AddEventType<UserRoleAssigned>();
            options.Events.AddEventType<UserRoleRemoved>();
            options.Events.AddEventType<UserClaimAdded>();
            options.Events.AddEventType<UserClaimRemoved>();
            options.Events.AddEventType<UserPasswordChanged>();
            options.Events.AddEventType<UserTwoFactorEnabled>();
            options.Events.AddEventType<UserTwoFactorDisabled>();
            options.Events.AddEventType<UserRecoveryCodesRegenerated>();
            options.Events.AddEventType<UserSessionsInvalidated>();
            options.Events.AddEventType<UserLoggedIn>();
            options.Events.AddEventType<UserLoginFailed>();
            options.Events.AddEventType<UserLockedOut>();
            options.Events.AddEventType<UserUnlocked>();
            options.Events.AddEventType<UserEmailConfirmed>();
            options.Events.AddEventType<UserPhoneNumberConfirmed>();

            // Register role events for the event store
            options.Events.AddEventType<RoleCreated>();
            options.Events.AddEventType<RoleNameChanged>();
            options.Events.AddEventType<RoleDescriptionChanged>();
            options.Events.AddEventType<RoleDeleted>();
            options.Events.AddEventType<RoleClaimAdded>();
            options.Events.AddEventType<RoleClaimRemoved>();

            // ═══════════════════════════════════════════════════════════════
            // ═══════════════════════════════════════════════════════════════
            // INLINE STATE PROJECTIONS (for validation, Identity, immediate consistency)
            // Naming Convention: *State = Inline projection, single source of truth
            // ═══════════════════════════════════════════════════════════════

            // UserState projection - runs inline for immediate consistency
            // Use for: validation, uniqueness checks, authentication, Identity stores
            options.Projections.Add(new UserStateProjection(), ProjectionLifecycle.Inline);

            // RoleState projection - runs inline for immediate consistency
            // Use for: role validation, claims lookup, Identity stores
            options.Projections.Add(new RoleStateProjection(), ProjectionLifecycle.Inline);

            // ═══════════════════════════════════════════════════════════════
            // ASYNC PROJECTIONS (for API responses, UI, eventually consistent)
            // ═══════════════════════════════════════════════════════════════

            // UserDetailsReadModel projection - runs async via daemon
            // Use for: API responses, admin UI, user listings, search results
            // Contains denormalized role info (name, description) - no security data
            options.Projections.Add(new UserDetailsProjection(), ProjectionLifecycle.Async);

            // ═══════════════════════════════════════════════════════════════
            // STATE MODEL INDEXES
            // ═══════════════════════════════════════════════════════════════

            // Configure UserState indexes for fast lookups
            options.Schema.For<UserState>()
                .Identity(x => x.Id)
                .Index(x => x.NormalizedUserName, x => x.IsUnique = true)
                .Index(x => x.NormalizedEmail);

            // Configure RoleState indexes for fast lookups
            options.Schema.For<RoleState>()
                .Identity(x => x.Id)
                .Index(x => x.NormalizedName, x => x.IsUnique = true);

            // Configure UserDetailsReadModel indexes
            options.Schema.For<UserDetailsReadModel>()
                .Identity(x => x.Id)
                .Index(x => x.Email)
                .Index(x => x.IsActive);
        })
        .UseLightweightSessions()
        .AddAsyncDaemon(DaemonMode.HotCold); // Enable async daemon for async projections

        // Register repositories
        services.AddScoped<IUserRepository, MartenUserRepository>();
        services.AddScoped<IRoleRepository, MartenRoleRepository>();

        // Register email sender
        services.AddSingleton<MockEmailSender>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<MockEmailSender>());

        // Register authentication service
        services.AddScoped<IAuthenticationService, AspNetCoreAuthenticationService>();

        // Register repositories
        services.AddScoped<IUserRepository, MartenUserRepository>();
        services.AddScoped<IRoleRepository, MartenRoleRepository>();
        services.AddScoped<IUserDetailsRepository, UserDetailsRepository>();

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
        .AddUserStore<EventSourcedUserStore>()
        .AddRoleStore<EventSourcedRoleStore>()
        .AddDefaultTokenProviders();
    }
}
