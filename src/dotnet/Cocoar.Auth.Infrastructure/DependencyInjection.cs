using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Domain.Aggregates;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Infrastructure.Identity;
using Cocoar.Auth.Infrastructure.Interfaces;
using Cocoar.Auth.Infrastructure.Persistence;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Cocoar.Auth.Infrastructure.Persistence.Repositories;
using Cocoar.Auth.Infrastructure.Repositories;
using Cocoar.Auth.Infrastructure.Services;
using Cocoar.Configuration.Reactive;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Weasel.Core.Migrations;
using StoreOptions = Marten.StoreOptions;

namespace Cocoar.Auth.Infrastructure;

public static class DependencyInjection
{
	public static IServiceCollection AddInfrastructure(
		this IServiceCollection services,
		bool useAsyncProjections = true,
		string? masterConnectionString = null,
		string? defaultConnectionString = null)
	{
		services.AddHttpContextAccessor();

		// Configure Marten
		var martenBuilder = services.AddMarten(sp =>
		{
			var options = new StoreOptions();

			if (masterConnectionString is not null && defaultConnectionString is not null)
			{
				// Multi-tenant mode: master DB + per-tenant databases
				options.MultiTenantedDatabasesWithMasterDatabaseTable(x =>
				{
					x.ConnectionString = masterConnectionString;
					x.SchemaName = "realms";
					x.AutoCreate = AutoCreate.CreateOrUpdate;
					x.ApplicationName = "CocoarAuth";
					x.RegisterDatabase("system", defaultConnectionString);
				});
			}
			else
			{
				// Legacy single-tenant mode (fallback for tests that haven't been updated)
				var dbSettings = sp.GetRequiredService<IReactiveConfig<IDatabaseSettings>>();
				var dataSourceBuilder = new NpgsqlDataSourceBuilder(dbSettings.CurrentValue.ConnectionString);

				dataSourceBuilder.UsePasswordProvider(
					(csb) =>
					{
						using var password = dbSettings.CurrentValue.Password.Open();
						return password.Value;
					},
					(csb, token) =>
					{
						using var password = dbSettings.CurrentValue.Password.Open();
						return ValueTask.FromResult(password.Value);
					});

				var dataSource = dataSourceBuilder.Build();
				options.Connection(dataSource);
			}

			options.AutoCreateSchemaObjects = AutoCreate.All;

			// Configure System.Text.Json to handle private setters
			options.UseSystemTextJsonForSerialization(configure: o =>
			{
				o.PropertyNamingPolicy = null; // Use exact property names
				o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
			});

			// Extracted: Document storage and event configuration
			ConfigureMartenDocuments(options);
			ConfigureMartenEvents(options, useAsyncProjections);
			return options;
		})
		.UseLightweightSessions()
		.ApplyAllDatabaseChangesOnStartup();

		// Override scoped session registrations with tenant-aware versions
		services.AddScoped<IDocumentSession>(sp =>
		{
			var store = sp.GetRequiredService<IDocumentStore>();
			var accessor = sp.GetRequiredService<IHttpContextAccessor>();
			var tenantId = accessor.HttpContext?.Items["TenantId"] as string ?? "system";
			return store.LightweightSession(tenantId);
		});

		services.AddScoped<IQuerySession>(sp =>
		{
			var store = sp.GetRequiredService<IDocumentStore>();
			var accessor = sp.GetRequiredService<IHttpContextAccessor>();
			var tenantId = accessor.HttpContext?.Items["TenantId"] as string ?? "system";
			return store.QuerySession(tenantId);
		});

		// Register tenant session factory
		services.AddScoped<ITenantSessionFactory, HttpContextTenantSessionFactory>();

		// Only enable async daemon when using async projections (not in tests)
		if (useAsyncProjections)
		{
			martenBuilder.AddAsyncDaemon(DaemonMode.HotCold);
		}

		// Register repositories and services
		RegisterRepositories(services);
		RegisterInfrastructureServices(services);

		return services;
	}

	/// <summary>
	/// Registers all repository implementations.
	/// </summary>
	private static void RegisterRepositories(IServiceCollection services)
	{
		services.AddScoped<IUserRepository, MartenUserRepository>();
		services.AddScoped<IRoleRepository, MartenRoleRepository>();
		services.AddScoped<ISessionRepository, MartenSessionRepository>();
		services.AddScoped<IUserDetailsRepository, UserDetailsRepository>();
		services.AddScoped<IOAuthApiResourceRepository, OAuthApiResourceRepository>();
		services.AddScoped<ILoginProviderRepository, LoginProviderRepository>();
	}

	/// <summary>
	/// Registers all infrastructure services (email, authentication, audit, device info, session, GDPR).
	/// </summary>
	private static void RegisterInfrastructureServices(IServiceCollection services)
	{
		// Register email sender
		services.AddSingleton<MockEmailSender>();
		services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<MockEmailSender>());

		// Register authentication service
		services.AddScoped<IAuthenticationService, AspNetCoreAuthenticationService>();

		// Register login audit service
		services.AddScoped<ILoginAuditService, LoginAuditService>();

		// Register device info service
		services.AddSingleton<IDeviceInfoService, DeviceInfoService>();

		// Register session service
		services.AddScoped<ISessionService, Cocoar.Auth.Application.Services.SessionService>();

		// Register GDPR service
		services.AddScoped<IGdprService, GdprService>();

		// Register Email OTP service
		services.AddScoped<IEmailOtpService, EmailOtpService>();

		// Register WebAuthn service
		services.AddScoped<IWebAuthnService, WebAuthnService>();
	}

	/// <summary>
	/// Configures Marten document storage schemas for non-event-sourced entities.
	/// </summary>
	private static void ConfigureMartenDocuments(StoreOptions options)
	{
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

		// Configure UserSession document (ephemeral state, not event-sourced)
		options.Schema.For<UserSession>()
			.Identity(x => x.Id)
			.Index(x => x.UserId)
			.Index(x => x.SessionId);

		// Configure EmailOtpChallenge document (ephemeral, for email OTP verification)
		options.Schema.For<EmailOtpChallenge>()
			.Identity(x => x.Id);

		// Configure WebAuthnChallenge document (ephemeral, for WebAuthn ceremonies)
		options.Schema.For<WebAuthnChallenge>()
			.Identity(x => x.Id)
			.Index(x => x.UserId);

		// ═══════════════════════════════════════════════════════════════
		// OPENIDDICT DOCUMENT STORAGE (Security-sensitive, not event-sourced)
		// ═══════════════════════════════════════════════════════════════

		// Configure OAuthApplicationSecurityData document (sensitive data like ClientSecret)
		options.Schema.For<OAuthApplicationSecurityData>()
			.Identity(x => x.Id);

		// Configure OpenIddict Authorization document (ephemeral, consent records)
		options.Schema.For<OpenIddictAuthorizationDocument>()
			.Identity(x => x.Id)
			.Index(x => x.ApplicationId)
			.Index(x => x.Subject);

		// Configure OpenIddict Token document (sensitive, ephemeral)
		options.Schema.For<OpenIddictTokenDocument>()
			.Identity(x => x.Id)
			.Index(x => x.ApplicationId)
			.Index(x => x.AuthorizationId)
			.Index(x => x.Subject)
			.Index(x => x.ReferenceId);

		// ═══════════════════════════════════════════════════════════════
		// REALM DOCUMENT STORAGE (stored in system tenant)
		// ═══════════════════════════════════════════════════════════════

		// Configure Realm document (tenant metadata, stored in system tenant DB)
		options.Schema.For<Realm>()
			.Identity(x => x.Id)
			.Index(x => x.Slug, x => x.IsUnique = true);
	}

	/// <summary>
	/// Configures Marten event types, masking rules, projections, and event-sourced indexes.
	/// </summary>
	private static void ConfigureMartenEvents(StoreOptions options, bool useAsyncProjections)
	{
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
		options.Events.AddEventType<UserExpirationChanged>();
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

		// Register Email OTP events
		options.Events.AddEventType<UserEmailOtpRequested>();
		options.Events.AddEventType<UserEmailOtpVerified>();

		// Register WebAuthn events
		options.Events.AddEventType<WebAuthnCredentialRegistered>();
		options.Events.AddEventType<WebAuthnCredentialDeleted>();
		options.Events.AddEventType<WebAuthnCredentialUsed>();

		// Register GDPR events for the event store
		options.Events.AddEventType<UserDeletionRequested>();
		options.Events.AddEventType<UserDeletionCancelled>();
		options.Events.AddEventType<UserDataMasked>();
		options.Events.AddEventType<UserDataExported>();
		options.Events.AddEventType<UserRestored>();

		// ═══════════════════════════════════════════════════════════════
		// GDPR DATA MASKING RULES
		// These rules define how PII is masked when ApplyEventDataMasking is called
		// ═══════════════════════════════════════════════════════════════

		// Mask PII in UserCreated events
		options.Events.AddMaskingRuleForProtectedInformation<UserCreated>(x =>
			new UserCreated(
				x.UserId,
				"[DELETED]",
				"[DELETED]",
				null,
				null,
				null,
				x.IsActive,
				x.LockoutEnabled,
				x.Roles));

		// Mask PII in UserNameChanged events
		options.Events.AddMaskingRuleForProtectedInformation<UserNameChanged>(x =>
			new UserNameChanged(x.UserId, "[DELETED]", "[DELETED]"));

		// Mask PII in UserEmailChanged events
		options.Events.AddMaskingRuleForProtectedInformation<UserEmailChanged>(x =>
			new UserEmailChanged(x.UserId, "[DELETED]", "[DELETED]"));

		// Mask PII in UserPhoneNumberChanged events
		options.Events.AddMaskingRuleForProtectedInformation<UserPhoneNumberChanged>(x =>
			new UserPhoneNumberChanged(x.UserId, null, null));

		// Mask PII in UserProfileNameChanged events
		options.Events.AddMaskingRuleForProtectedInformation<UserProfileNameChanged>(x =>
			new UserProfileNameChanged(x.UserId, null, null, null, null));

		// Mask IP addresses in login events (considered PII under GDPR)
		options.Events.AddMaskingRuleForProtectedInformation<UserLoggedIn>(x =>
			new UserLoggedIn(x.UserId, null, null));

		options.Events.AddMaskingRuleForProtectedInformation<UserLoginFailed>(x =>
			new UserLoginFailed(x.UserId, null, null, x.FailureReason));

		// Register role events for the event store
		options.Events.AddEventType<RoleCreated>();
		options.Events.AddEventType<RoleNameChanged>();
		options.Events.AddEventType<RoleDescriptionChanged>();
		options.Events.AddEventType<RoleDeleted>();
		options.Events.AddEventType<RoleClaimAdded>();
		options.Events.AddEventType<RoleClaimRemoved>();
		options.Events.AddEventType<RoleDisplayNameChanged>();
		options.Events.AddEventType<RoleEmailChanged>();
		options.Events.AddEventType<RoleBoundToApiResourceChanged>();

		// Register OAuth application events for the event store
		options.Events.AddEventType<OAuthApplicationCreated>();
		options.Events.AddEventType<OAuthApplicationDisplayNameChanged>();
		options.Events.AddEventType<OAuthApplicationClientTypeChanged>();
		options.Events.AddEventType<OAuthApplicationConsentTypeChanged>();
		options.Events.AddEventType<OAuthApplicationRedirectUrisChanged>();
		options.Events.AddEventType<OAuthApplicationPostLogoutRedirectUrisChanged>();
		options.Events.AddEventType<OAuthApplicationPermissionsChanged>();
		options.Events.AddEventType<OAuthApplicationRequirementsChanged>();
		options.Events.AddEventType<OAuthApplicationSettingsChanged>();
		options.Events.AddEventType<OAuthApplicationDisplayNamesChanged>();
		options.Events.AddEventType<OAuthApplicationPropertiesChanged>();
		options.Events.AddEventType<OAuthApplicationDeleted>();

		// Register OAuth scope events for the event store
		options.Events.AddEventType<OAuthScopeCreated>();
		options.Events.AddEventType<OAuthScopeDisplayNameChanged>();
		options.Events.AddEventType<OAuthScopeDescriptionChanged>();
		options.Events.AddEventType<OAuthScopeResourcesChanged>();
		options.Events.AddEventType<OAuthScopeDisplayNamesChanged>();
		options.Events.AddEventType<OAuthScopeDescriptionsChanged>();
		options.Events.AddEventType<OAuthScopePropertiesChanged>();
		options.Events.AddEventType<OAuthScopeEnabledChanged>();
		options.Events.AddEventType<OAuthScopeRequiredChanged>();
		options.Events.AddEventType<OAuthScopeEmphasizeChanged>();
		options.Events.AddEventType<OAuthScopeShowInDiscoveryDocumentChanged>();
		options.Events.AddEventType<OAuthScopeUserClaimsChanged>();
		options.Events.AddEventType<OAuthScopeDeleted>();

		// Register login provider events for the event store
		options.Events.AddEventType<LoginProviderCreated>();
		options.Events.AddEventType<LoginProviderNameChanged>();
		options.Events.AddEventType<LoginProviderDisplayNameChanged>();
		options.Events.AddEventType<LoginProviderDescriptionChanged>();
		options.Events.AddEventType<LoginProviderConfigurationChanged>();
		options.Events.AddEventType<LoginProviderDeleted>();

		// Register OAuth API resource events for the event store
		options.Events.AddEventType<OAuthApiResourceCreated>();
		options.Events.AddEventType<OAuthApiResourceDisplayNameChanged>();
		options.Events.AddEventType<OAuthApiResourceDescriptionChanged>();
		options.Events.AddEventType<OAuthApiResourceEnabled>();
		options.Events.AddEventType<OAuthApiResourceDisabled>();
		options.Events.AddEventType<OAuthApiResourceScopesChanged>();
		options.Events.AddEventType<OAuthApiResourceUserClaimsChanged>();
		options.Events.AddEventType<OAuthApiResourcePropertiesChanged>();
		options.Events.AddEventType<OAuthApiResourceDeleted>();

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

		// OAuthApplicationState projection - runs inline for immediate consistency
		// Use for: OpenIddict store operations, validation
		options.Projections.Add(new OAuthApplicationStateProjection(), ProjectionLifecycle.Inline);

		// OAuthScopeState projection - runs inline for immediate consistency
		// Use for: OpenIddict store operations, validation
		options.Projections.Add(new OAuthScopeStateProjection(), ProjectionLifecycle.Inline);

		// OAuthApiResourceState projection - runs inline for immediate consistency
		// Use for: API resource management, introspection validation
		options.Projections.Add(new OAuthApiResourceStateProjection(), ProjectionLifecycle.Inline);

		// LoginProviderState projection - runs inline for immediate consistency
		// Use for: login provider validation, lookups
		options.Projections.Add(new LoginProviderStateProjection(), ProjectionLifecycle.Inline);

		// ═══════════════════════════════════════════════════════════════
		// READ MODEL PROJECTIONS (configurable: async for prod, inline for tests)
		// ═══════════════════════════════════════════════════════════════

		// UserDetailsReadModel projection
		// Use for: API responses, admin UI, user listings, search results
		// Contains denormalized role info (name, description) - no security data
		// Async mode uses daemon (eventually consistent), Inline mode runs synchronously
		var readModelLifecycle = useAsyncProjections ? ProjectionLifecycle.Async : ProjectionLifecycle.Inline;
		options.Projections.Add(new UserDetailsProjection(), readModelLifecycle);

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

		// Configure OAuthApplicationState indexes for fast lookups
		options.Schema.For<OAuthApplicationState>()
			.Identity(x => x.Id)
			.Index(x => x.ClientId, x => x.IsUnique = true);

		// Configure OAuthScopeState indexes for fast lookups
		options.Schema.For<OAuthScopeState>()
			.Identity(x => x.Id)
			.Index(x => x.Name, x => x.IsUnique = true);

		// Configure OAuthApiResourceState indexes for fast lookups
		options.Schema.For<OAuthApiResourceState>()
			.Identity(x => x.Id)
			.Index(x => x.Name, x => x.IsUnique = true);

		// Configure LoginProviderState indexes for fast lookups
		options.Schema.For<LoginProviderState>()
			.Identity(x => x.Id)
			.Index(x => x.Name, x => x.IsUnique = true);

		// Configure OAuthApiResourceSecurityData document (security-sensitive data, not event-sourced)
		options.Schema.For<OAuthApiResourceSecurityData>()
			.Identity(x => x.Id);
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
