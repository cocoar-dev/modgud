using System.Text.Json;
using System.Text.Json.Serialization;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Infrastructure.Identity;
using Cocoar.Auth.Infrastructure.Interfaces;
using Cocoar.Auth.Infrastructure.Persistence;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Async = Cocoar.Auth.Infrastructure.Persistence.Projections.Async;
using Cocoar.Auth.Infrastructure.Persistence.Repositories;
using Cocoar.Auth.Infrastructure.Repositories;
using Cocoar.Auth.Infrastructure.Services;
using Cocoar.Configuration.Core;
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
// ABAC namespace aliases
using Abac = Cocoar.Auth.Application.Authorization;
using AbacInf = Cocoar.Auth.Infrastructure.Authorization;
using AbacEvents = Cocoar.Auth.Domain.Authorization.Events;
using Cocoar.JsEval.Engine;
using Cocoar.JsEval.Linq;
using Cocoar.JsEval.TsDefinition;
using Cocoar.JsEval.TypeScript;

namespace Cocoar.Auth.Infrastructure;

public static class DependencyInjection
{
	/// <summary>
	/// Configures Marten StoreOptions for the auth system.
	/// Called from Program.cs inside UseWolverine() for IntegrateWithWolverine compatibility.
	/// Single DB architecture: the main connection is both tenant registry AND system tenant.
	/// </summary>
	public static StoreOptions ConfigureMartenOptions(
		string connectionString,
		bool useAsyncProjections)
	{
		var options = new StoreOptions();

		// Multi-tenant mode: master table tenancy in the same global DB.
		// No RegisterDatabase needed — tenants are registered dynamically via AddDatabaseRecordAsync.
		// Realm documents are stored in IGlobalStore (non-tenanted), not here.
		options.MultiTenantedDatabasesWithMasterDatabaseTable(x =>
		{
			x.ConnectionString = connectionString;
			x.SchemaName = "realms";
			x.AutoCreate = AutoCreate.CreateOrUpdate;
			x.ApplicationName = "CocoarAuth";
		});

		options.AutoCreateSchemaObjects = AutoCreate.All;

		// Enable side effects in inline projections — allows RaiseSideEffects()
		// to publish messages via Wolverine after the projection commits.
		options.Events.EnableSideEffectsOnInlineProjections = true;

		// Configure System.Text.Json to handle private setters
		options.UseSystemTextJsonForSerialization(configure: o =>
		{
			o.PropertyNamingPolicy = null; // Use exact property names
			o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
			o.Converters.Add(new JsonStringEnumConverter());
		}, enumStorage: Weasel.Core.EnumStorage.AsString);

		// Document storage and event configuration
		ConfigureMartenDocuments(options);
		ConfigureMartenEvents(options, useAsyncProjections);
		return options;
	}

	/// <summary>
	/// Registers the global (non-tenanted) Marten DocumentStore for cross-tenant data like Realm.
	/// Uses the same database as the tenanted store's master table.
	/// </summary>
	public static IServiceCollection AddGlobalStore(this IServiceCollection services, string connectionString)
	{
		services.AddMartenStore<IGlobalStore>(opts =>
		{
			opts.Connection(connectionString);
			opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

			opts.Schema.For<Realm>()
				.Identity(x => x.Id)
				.Index(x => x.Slug, x => { x.IsUnique = true; x.Predicate = "((data ->> 'IsActive')::boolean = true)"; });

			opts.UseSystemTextJsonForSerialization(configure: o =>
			{
				o.PropertyNamingPolicy = null;
				o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
				o.Converters.Add(new JsonStringEnumConverter());
			});
		}).ApplyAllDatabaseChangesOnStartup();

		return services;
	}

	public static IServiceCollection AddInfrastructure(this IServiceCollection services)
	{
		services.AddHttpContextAccessor();

		// Register tenant session factory
		services.AddScoped<ITenantSessionFactory, HttpContextTenantSessionFactory>();

		// Register HttpClient factory for OIDC protocol operations
		services.AddHttpClient();

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
		services.AddScoped<IOAuthApiRepository, OAuthApiRepository>();
		services.AddScoped<ILoginProviderRepository, LoginProviderRepository>();
		services.AddScoped<IUserListRepository, UserListRepository>();
		services.AddScoped<IRoleListRepository, RoleListRepository>();
		services.AddScoped<IGroupListRepository, GroupListRepository>();
		services.AddScoped<IGroupRepository, GroupRepository>();
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

		// Register OIDC protocol service
		services.AddSingleton<IOidcProtocolService, OidcProtocolService>();

		// Register external login service
		services.AddScoped<IExternalLoginService, ExternalLoginService>();

		// Register event appender
		services.AddScoped<IEventAppender, MartenEventAppender>();

		// Register effective roles service (resolves direct + group-inherited roles)
		services.AddScoped<IEffectiveRolesService, EffectiveRolesService>();

		// ── ABAC services (script-based authorization, see Domain/Authorization) ──
		services.AddJsEval(b => b.AddLinq());
		services.AddTsTranspiler();
		services.AddTsDefinition();
		services.AddScoped<Abac.IPermissionService, AbacInf.PermissionService>();
		services.AddScoped<Abac.IAccessPolicyEngine, AbacInf.AccessPolicyEngine>();
		services.AddScoped<Abac.IMembershipEvaluator, AbacInf.MembershipEvaluator>();
		services.AddScoped<AbacInf.AutoMembershipRecalculator>();
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
			.Index(x => x.NormalizedUserName!, x => { x.IsUnique = true; x.Predicate = "((data ->> 'IsDeleted')::boolean IS NOT TRUE)"; })
			.Index(x => x.NormalizedEmail!);

		// Configure ApplicationRole document
		options.Schema.For<ApplicationRole>()
			.Identity(x => x.Id)
			.Index(x => x.NormalizedName, x => { x.IsUnique = true; x.Predicate = "((data ->> 'IsDeleted')::boolean IS NOT TRUE)"; });

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

		// Configure ExternalLoginState document (ephemeral, for OIDC login flows)
		options.Schema.For<ExternalLoginState>()
			.Identity(x => x.Id)
			.Index(x => x.State);

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

		// NOTE: Realm documents are stored in IGlobalStore (non-tenanted), not in
		// the tenanted store. See AddGlobalStore() for Realm schema configuration.
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

		// Register External Login events
		options.Events.AddEventType<UserExternalLoginLinked>();
		options.Events.AddEventType<UserExternalLoginRemoved>();

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
		options.Events.AddEventType<RoleClientChanged>();
		options.Events.AddEventType<RoleScopesChanged>();

		// Register group events
		options.Events.AddEventType<GroupCreated>();
		options.Events.AddEventType<GroupRenamed>();
		options.Events.AddEventType<GroupDescriptionChanged>();
		options.Events.AddEventType<GroupArchived>();
		options.Events.AddEventType<GroupMemberAdded>();
		options.Events.AddEventType<GroupMemberRemoved>();
		options.Events.AddEventType<GroupChildAdded>();
		options.Events.AddEventType<GroupChildRemoved>();
		options.Events.AddEventType<GroupRealmRoleGranted>();
		options.Events.AddEventType<GroupRealmRoleRevoked>();
		options.Events.AddEventType<GroupClientRoleGranted>();
		options.Events.AddEventType<GroupClientRoleRevoked>();

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

		// Register OAuth API events for the event store
		options.Events.AddEventType<OAuthApiCreated>();
		options.Events.AddEventType<OAuthApiDisplayNameChanged>();
		options.Events.AddEventType<OAuthApiDescriptionChanged>();
		options.Events.AddEventType<OAuthApiEnabled>();
		options.Events.AddEventType<OAuthApiDisabled>();
		options.Events.AddEventType<OAuthApiScopesChanged>();
		options.Events.AddEventType<OAuthApiUserClaimsChanged>();
		options.Events.AddEventType<OAuthApiPropertiesChanged>();
		options.Events.AddEventType<OAuthApiDeleted>();

		// Register ABAC events (script-based authorization system)
		options.Events.AddEventType<AbacEvents.PermissionRoleCreatedEvent>();
		options.Events.AddEventType<AbacEvents.PermissionRoleUpdatedEvent>();
		options.Events.AddEventType<AbacEvents.PermissionRoleDeletedEvent>();
		options.Events.AddEventType<AbacEvents.AuthorizationGroupCreatedEvent>();
		options.Events.AddEventType<AbacEvents.AuthorizationGroupUpdatedEvent>();
		options.Events.AddEventType<AbacEvents.AuthorizationGroupDeletedEvent>();
		options.Events.AddEventType<AbacEvents.AuthorizationGroupMembershipRecomputedEvent>();
		options.Events.AddEventType<AbacEvents.AuthorizationGroupMembershipRecomputeFailedEvent>();

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

		// OAuthApiState projection - runs inline for immediate consistency
		// Use for: API management, introspection validation
		options.Projections.Add(new OAuthApiStateProjection(), ProjectionLifecycle.Inline);

		// LoginProviderState projection - runs inline for immediate consistency
		// Use for: login provider validation, lookups
		options.Projections.Add(new LoginProviderStateProjection(), ProjectionLifecycle.Inline);
		options.Projections.Add(new GroupStateProjection(), ProjectionLifecycle.Inline);

		// ABAC projections — inline because permission checks need immediate consistency
		options.Projections.Add<AbacInf.PermissionRoleProjection>(ProjectionLifecycle.Inline);
		options.Projections.Add<AbacInf.AuthorizationGroupProjection>(ProjectionLifecycle.Inline);

		// PrincipalDirectory — cross-type inline projection (Person + Group). Backs
		// auto-membership predicate evaluation and cross-type principal lookup.
		options.Projections.Add<AbacInf.PrincipalDirectoryProjection>(ProjectionLifecycle.Inline);
		options.Schema.For<AbacInf.PrincipalDirectory>()
			.Identity(x => x.Id)
			.Index(x => x.Type)
			.Index(x => x.IsDeleted)
			.Index(x => x.NormalizedEmail);

		// ═══════════════════════════════════════════════════════════════
		// READ MODEL PROJECTIONS (configurable: async for prod, inline for tests)
		// ═══════════════════════════════════════════════════════════════

		// UserDetailsReadModel projection
		// Use for: API responses, admin UI, user listings, search results
		// Contains denormalized role info (name, description) - no security data
		// Async mode uses daemon (eventually consistent), Inline mode runs synchronously
		var readModelLifecycle = useAsyncProjections ? ProjectionLifecycle.Async : ProjectionLifecycle.Inline;
		options.Projections.Add(new UserDetailsProjection(), readModelLifecycle);
		options.Projections.Add(new Async.UserListProjection(), readModelLifecycle);
		options.Projections.Add(new Async.RoleListProjection(), readModelLifecycle);
		options.Projections.Add(new Async.GroupListProjection(), readModelLifecycle);

		// ═══════════════════════════════════════════════════════════════
		// STATE MODEL INDEXES
		// ═══════════════════════════════════════════════════════════════

		// Configure UserState indexes for fast lookups
		options.Schema.For<UserState>()
			.Identity(x => x.Id)
			.Index(x => x.NormalizedUserName, x => { x.IsUnique = true; x.Predicate = "((data ->> 'IsDeleted')::boolean IS NOT TRUE)"; })
			.Index(x => x.NormalizedEmail);

		// Configure RoleState indexes for fast lookups
		options.Schema.For<RoleState>()
			.Identity(x => x.Id)
			.Index(x => x.NormalizedName, x => { x.IsUnique = true; x.Predicate = "((data ->> 'IsDeleted')::boolean IS NOT TRUE)"; });

		// Configure UserDetailsReadModel indexes
		options.Schema.For<UserDetailsReadModel>()
			.Identity(x => x.Id)
			.Index(x => x.Email)
			.Index(x => x.IsActive);

		// Configure UserListReadModel indexes
		options.Schema.For<Application.ReadModels.UserListReadModel>()
			.Identity(x => x.Id)
			.Index(x => x.UserName)
			.Index(x => x.Email)
			.Index(x => x.IsActive);

		// Configure RoleListReadModel indexes
		options.Schema.For<Application.ReadModels.RoleListReadModel>()
			.Identity(x => x.Id)
			.Index(x => x.Name);

		// Configure OAuthApplicationState indexes for fast lookups
		options.Schema.For<OAuthApplicationState>()
			.Identity(x => x.Id)
			.Index(x => x.ClientId, x => { x.IsUnique = true; x.Predicate = "((data ->> 'IsDeleted')::boolean IS NOT TRUE)"; });

		// Configure OAuthScopeState indexes for fast lookups
		options.Schema.For<OAuthScopeState>()
			.Identity(x => x.Id)
			.Index(x => x.Name, x => { x.IsUnique = true; x.Predicate = "((data ->> 'IsDeleted')::boolean IS NOT TRUE)"; });

		// Configure OAuthApiState indexes for fast lookups
		options.Schema.For<OAuthApiState>()
			.Identity(x => x.Id)
			.Index(x => x.Name, x => { x.IsUnique = true; x.Predicate = "((data ->> 'IsDeleted')::boolean IS NOT TRUE)"; });

		// Configure GroupState indexes
		options.Schema.For<Application.Models.GroupState>()
			.Identity(x => x.Id)
			.Index(x => x.Name);

		// Configure GroupListReadModel indexes
		options.Schema.For<Application.ReadModels.GroupListReadModel>()
			.Identity(x => x.Id)
			.Index(x => x.Name);

		// Configure LoginProviderState indexes for fast lookups
		options.Schema.For<LoginProviderState>()
			.Identity(x => x.Id)
			.Index(x => x.Name, x => { x.IsUnique = true; x.Predicate = "((data ->> 'IsDeleted')::boolean IS NOT TRUE)"; });

		// Configure OAuthApiSecurityData document (security-sensitive data, not event-sourced)
		options.Schema.For<OAuthApiSecurityData>()
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

			// Lockout settings — 1-minute lockout minimizes targeted DoS impact
			// while still protecting against brute force (knowledge base best practice)
			options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(1);
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
