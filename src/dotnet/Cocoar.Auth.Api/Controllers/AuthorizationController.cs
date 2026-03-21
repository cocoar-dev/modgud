using System.Security.Claims;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Marten;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Api.Controllers;

/// <summary>
/// Handles OpenID Connect / OAuth 2.0 authorization endpoints.
/// </summary>
public class AuthorizationController : Controller
{
	private readonly IOpenIddictApplicationManager _applicationManager;
	private readonly IOpenIddictAuthorizationManager _authorizationManager;
	private readonly IOpenIddictScopeManager _scopeManager;
	private readonly SignInManager<ApplicationUser> _signInManager;
	private readonly UserManager<ApplicationUser> _userManager;
	private readonly IRoleRepository _roleRepository;
	private readonly IQuerySession _querySession;

	public AuthorizationController(
		IOpenIddictApplicationManager applicationManager,
		IOpenIddictAuthorizationManager authorizationManager,
		IOpenIddictScopeManager scopeManager,
		SignInManager<ApplicationUser> signInManager,
		UserManager<ApplicationUser> userManager,
		IRoleRepository roleRepository,
		IQuerySession querySession)
	{
		_applicationManager = applicationManager;
		_authorizationManager = authorizationManager;
		_scopeManager = scopeManager;
		_signInManager = signInManager;
		_userManager = userManager;
		_roleRepository = roleRepository;
		_querySession = querySession;
	}

	/// <summary>
	/// Authorization endpoint - processes authorization requests.
	/// </summary>
	[HttpGet("~/connect/authorize")]
	[HttpPost("~/connect/authorize")]
	[IgnoreAntiforgeryToken]
	public async Task<IActionResult> Authorize()
	{
		var request = HttpContext.GetOpenIddictServerRequest() ??
			throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

		// If the user is not authenticated, redirect to the login page
		var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);

		// Check if user needs to be redirected to login
		var needsLogin = !result.Succeeded;

		// Check if session is too old based on max_age parameter
		if (result.Succeeded && request.MaxAge != null && result.Properties?.IssuedUtc != null)
		{
			var sessionAge = DateTimeOffset.UtcNow - result.Properties.IssuedUtc.Value;
			if (sessionAge > TimeSpan.FromSeconds(request.MaxAge.Value))
			{
				needsLogin = true;
			}
		}

		// Check if prompt=login was requested
		var prompt = request.Prompt;
		if (!string.IsNullOrEmpty(prompt) && prompt.Contains("login", StringComparison.OrdinalIgnoreCase))
		{
			needsLogin = true;
		}

		if (needsLogin)
		{
			return Challenge(
				authenticationSchemes: IdentityConstants.ApplicationScheme,
				properties: new AuthenticationProperties
				{
					RedirectUri = Request.PathBase + Request.Path + Request.QueryString
				});
		}

		// Retrieve the application details from the database
		var application = await _applicationManager.FindByClientIdAsync(request.ClientId!) ??
			throw new InvalidOperationException("Details concerning the calling client application cannot be found.");

		// Retrieve the user from the stored claims principal
		var user = await _userManager.GetUserAsync(result.Principal!);
		if (user is null)
		{
			return Challenge(
				authenticationSchemes: IdentityConstants.ApplicationScheme,
				properties: new AuthenticationProperties
				{
					RedirectUri = Request.PathBase + Request.Path + Request.QueryString
				});
		}

		// Check if the user has been deactivated
		if (!user.IsActive || user.IsDeleted)
		{
			return Forbid(
				authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
				properties: new AuthenticationProperties(new Dictionary<string, string?>
				{
					[OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
					[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user account has been deactivated."
				}));
		}

		// Retrieve the permanent authorizations associated with the user and the calling application
		var authorizations = await _authorizationManager.FindAsync(
			subject: await _userManager.GetUserIdAsync(user),
			client: await _applicationManager.GetIdAsync(application) ?? string.Empty,
			status: Statuses.Valid,
			type: AuthorizationTypes.Permanent,
			scopes: request.GetScopes()).ToListAsync();

		var consentType = await _applicationManager.GetConsentTypeAsync(application);

		// For implicit consent or if an authorization already exists, proceed without consent form
		if (consentType == ConsentTypes.Implicit || authorizations.Count != 0)
		{
			var principal = await CreateClaimsPrincipalAsync(user, request);

			// Create or reuse a permanent authorization
			var authorization = authorizations.LastOrDefault();
			authorization ??= await _authorizationManager.CreateAsync(
				principal: principal,
				subject: await _userManager.GetUserIdAsync(user),
				client: await _applicationManager.GetIdAsync(application) ?? string.Empty,
				type: AuthorizationTypes.Permanent,
				scopes: principal.GetScopes());

			principal.SetAuthorizationId(await _authorizationManager.GetIdAsync(authorization));

			return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
		}

		// For explicit consent with prompt=none, return error
		if (!string.IsNullOrEmpty(prompt) && prompt.Contains("none", StringComparison.OrdinalIgnoreCase))
		{
			return Forbid(
				authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
				properties: new AuthenticationProperties(new Dictionary<string, string?>
				{
					[OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.ConsentRequired,
					[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
						"Interactive user consent is required."
				}));
		}

		// Redirect to the consent page so the user can approve or deny the requested scopes
		var authorizeUrl = Request.PathBase + Request.Path + Request.QueryString;
		var consentUrl = $"/consent?returnUrl={Uri.EscapeDataString(authorizeUrl)}";
		return Redirect(consentUrl);
	}

	/// <summary>
	/// Token endpoint - exchanges authorization code or refresh token for tokens.
	/// </summary>
	[HttpPost("~/connect/token")]
	[IgnoreAntiforgeryToken]
	[Produces("application/json")]
	public async Task<IActionResult> Exchange()
	{
		var request = HttpContext.GetOpenIddictServerRequest() ??
			throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

		if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
		{
			// Retrieve the claims principal stored in the authorization code/refresh token
			var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

			// Retrieve the user profile corresponding to the authorization code/refresh token
			var subject = result.Principal?.GetClaim(Claims.Subject);
			if (string.IsNullOrEmpty(subject))
			{
				return Forbid(
					authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
					properties: new AuthenticationProperties(new Dictionary<string, string?>
					{
						[OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
						[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The token is no longer valid."
					}));
			}

			var user = await _userManager.FindByIdAsync(subject);
			if (user is null)
			{
				return Forbid(
					authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
					properties: new AuthenticationProperties(new Dictionary<string, string?>
					{
						[OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
						[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The token is no longer valid."
					}));
			}

			// Ensure the user is still allowed to sign in
			if (!await _signInManager.CanSignInAsync(user) || !user.IsActive || user.IsDeleted)
			{
				return Forbid(
					authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
					properties: new AuthenticationProperties(new Dictionary<string, string?>
					{
						[OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
						[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user is no longer allowed to sign in."
					}));
			}

			// Use scopes from the original authorization code / refresh token principal,
			// since the token exchange request does not include the scope parameter.
			var originalScopes = result.Principal?.GetScopes();
			var principal = await CreateClaimsPrincipalAsync(user, request, originalScopes);

			// Set the existing authorization id
			principal.SetAuthorizationId(result.Principal?.GetAuthorizationId());

			return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
		}

		if (request.IsDeviceCodeGrantType())
		{
			// Device code flow: the principal is populated by OpenIddict
			// from the verification step (when the user approved)
			var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
			var subject = result.Principal?.GetClaim(Claims.Subject);
			if (string.IsNullOrEmpty(subject))
			{
				return Forbid(
					authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
					properties: new AuthenticationProperties(new Dictionary<string, string?>
					{
						[OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
						[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The device code is no longer valid."
					}));
			}

			var user = await _userManager.FindByIdAsync(subject);
			if (user is null || !user.IsActive || user.IsDeleted)
			{
				return Forbid(
					authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
					properties: new AuthenticationProperties(new Dictionary<string, string?>
					{
						[OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
						[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user is no longer allowed to sign in."
					}));
			}

			var originalScopes = result.Principal?.GetScopes();
			var principal = await CreateClaimsPrincipalAsync(user, request, originalScopes);
			principal.SetAuthorizationId(result.Principal?.GetAuthorizationId());

			return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
		}

		if (request.IsClientCredentialsGrantType())
		{
			// For client credentials grant, create a minimal principal for the client
			var application = await _applicationManager.FindByClientIdAsync(request.ClientId!);
			if (application is null)
			{
				throw new InvalidOperationException("The application cannot be found.");
			}

			var identity = new ClaimsIdentity(
				authenticationType: TokenValidationParameters.DefaultAuthenticationType,
				nameType: Claims.Name,
				roleType: Claims.Role);

			// Use the client_id as the subject identifier
			identity.SetClaim(Claims.Subject, await _applicationManager.GetClientIdAsync(application));
			identity.SetClaim(Claims.Name, await _applicationManager.GetDisplayNameAsync(application));

			identity.SetScopes(request.GetScopes());
			// Include the client's own client_id as a resource so it can
			// introspect its own tokens and receive the full set of claims.
			var clientResources = await _scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync();
			var clientId = await _applicationManager.GetClientIdAsync(application);
			if (!string.IsNullOrEmpty(clientId) && !clientResources.Contains(clientId))
			{
				clientResources.Add(clientId);
			}
			identity.SetResources(clientResources);

			// Add client's configured claims and roles from application Properties.
			// Clients can have "cocoar:roles" and "cocoar:client_claims" in their Properties.
			var properties = await _applicationManager.GetPropertiesAsync(application);
			if (properties is not null)
			{
				// Add client roles as "role" claims
				if (properties.TryGetValue("cocoar:roles", out var rolesElement)
					&& rolesElement.ValueKind == System.Text.Json.JsonValueKind.Array)
				{
					foreach (var roleEl in rolesElement.EnumerateArray())
					{
						var roleName = roleEl.GetString();
						if (!string.IsNullOrEmpty(roleName))
						{
							identity.AddClaim(new Claim(Claims.Role, roleName));
						}
					}
				}

				// Add client custom claims (array of {type, value} objects)
				if (properties.TryGetValue("cocoar:client_claims", out var claimsElement)
					&& claimsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
				{
					foreach (var claimEl in claimsElement.EnumerateArray())
					{
						var claimType = claimEl.TryGetProperty("Type", out var typeEl) ? typeEl.GetString() : null;
						var claimValue = claimEl.TryGetProperty("Value", out var valueEl) ? valueEl.GetString() : null;
						if (!string.IsNullOrEmpty(claimType) && claimValue is not null)
						{
							identity.AddClaim(new Claim(claimType, claimValue));
						}
					}
				}
			}

			identity.SetDestinations(static claim => claim.Type switch
			{
				Claims.Name or Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
				_ => [Destinations.AccessToken]
			});

			return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
		}

		throw new InvalidOperationException("The specified grant type is not supported.");
	}

	/// <summary>
	/// UserInfo endpoint - returns claims about the authenticated user.
	/// </summary>
	[Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
	[HttpGet("~/connect/userinfo")]
	[HttpPost("~/connect/userinfo")]
	[Produces("application/json")]
	public async Task<IActionResult> Userinfo()
	{
		var subject = User.GetClaim(Claims.Subject);
		if (string.IsNullOrEmpty(subject))
		{
			return Challenge(
				authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
				properties: new AuthenticationProperties(new Dictionary<string, string?>
				{
					[OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
					[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The specified access token is invalid."
				}));
		}

		var user = await _userManager.FindByIdAsync(subject);
		if (user is null)
		{
			return Challenge(
				authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
				properties: new AuthenticationProperties(new Dictionary<string, string?>
				{
					[OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
					[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The specified access token is bound to an account that no longer exists."
				}));
		}

		var claims = new Dictionary<string, object>(StringComparer.Ordinal)
		{
			[Claims.Subject] = user.Id.ToString()
		};

		if (User.HasScope(Scopes.Email))
		{
			if (!string.IsNullOrEmpty(user.Email))
			{
				claims[Claims.Email] = user.Email;
				claims[Claims.EmailVerified] = user.EmailConfirmed;
			}
		}

		if (User.HasScope(Scopes.Profile))
		{
			claims[Claims.PreferredUsername] = user.UserName;
			claims[Claims.Name] = GetDisplayName(user);

			if (!string.IsNullOrEmpty(user.FirstName))
			{
				claims[Claims.GivenName] = user.FirstName;
			}

			if (!string.IsNullOrEmpty(user.LastName))
			{
				claims[Claims.FamilyName] = user.LastName;
			}
		}

		if (User.HasScope(Scopes.Phone))
		{
			if (!string.IsNullOrEmpty(user.PhoneNumber))
			{
				claims[Claims.PhoneNumber] = user.PhoneNumber;
				claims[Claims.PhoneNumberVerified] = user.PhoneNumberConfirmed;
			}
		}

		if (User.HasScope(Scopes.Roles))
		{
			var roles = await GetUserRoleNamesAsync(user);
			if (roles.Any())
			{
				claims[Claims.Role] = roles;
			}
		}

		// Include custom user claims based on scope and API UserClaims configuration
		var userScopes = User.GetScopes();
		var allowedClaimTypes = await GetAllowedClaimTypesAsync(userScopes);
		if (allowedClaimTypes.Count > 0 && user.Claims.Count > 0)
		{
			foreach (var userClaim in user.Claims)
			{
				if (allowedClaimTypes.Contains(userClaim.Type))
				{
					// If multiple claims of the same type exist, collect them as an array
					if (claims.TryGetValue(userClaim.Type, out var existing))
					{
						if (existing is List<string> list)
						{
							list.Add(userClaim.Value);
						}
						else
						{
							claims[userClaim.Type] = new List<string> { existing.ToString()!, userClaim.Value };
						}
					}
					else
					{
						claims[userClaim.Type] = userClaim.Value;
					}
				}
			}
		}

		return Ok(claims);
	}

	/// <summary>
	/// Verification endpoint - handles device code user verification.
	/// The user enters their code and approves/denies the request.
	/// </summary>
	[Authorize]
	[HttpGet("~/connect/verify")]
	[HttpPost("~/connect/verify")]
	[IgnoreAntiforgeryToken]
	public async Task<IActionResult> Verify()
	{
		var request = HttpContext.GetOpenIddictServerRequest() ??
			throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

		// If this is a GET request, redirect to the frontend verification page
		if (HttpContext.Request.Method == HttpMethods.Get)
		{
			var userCode = request.UserCode;
			var verifyUrl = $"/device?user_code={Uri.EscapeDataString(userCode ?? "")}";
			return Redirect(verifyUrl);
		}

		// POST: Process the verification (approve/deny)
		var user = await _userManager.GetUserAsync(User);
		if (user is null)
		{
			return Challenge(
				authenticationSchemes: IdentityConstants.ApplicationScheme,
				properties: new AuthenticationProperties
				{
					RedirectUri = Request.PathBase + Request.Path + Request.QueryString
				});
		}

		// Check if user denied
		if (!string.IsNullOrEmpty(Request.Form["deny"]))
		{
			return Forbid(
				authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
				properties: new AuthenticationProperties(new Dictionary<string, string?>
				{
					[OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
					[OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user denied the device authorization request."
				}));
		}

		// User approved — create claims principal
		var principal = await CreateClaimsPrincipalAsync(user, request);

		return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
	}

	/// <summary>
	/// Logout endpoint - signs the user out and redirects to the post-logout redirect URI.
	/// </summary>
	[HttpGet("~/connect/logout")]
	[HttpPost("~/connect/logout")]
	public async Task<IActionResult> Logout()
	{
		// Sign out of the identity application cookie
		await _signInManager.SignOutAsync();

		// Return a response to the calling application
		return SignOut(
			authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
			properties: new AuthenticationProperties
			{
				RedirectUri = "/"
			});
	}

	/// <summary>
	/// Creates a ClaimsPrincipal for the user with the appropriate claims based on requested scopes.
	/// </summary>
	private async Task<ClaimsPrincipal> CreateClaimsPrincipalAsync(
		ApplicationUser user,
		OpenIddictRequest request,
		IEnumerable<string>? scopeOverrides = null)
	{
		// Create a new identity with the authentication type expected by OpenIddict.
		// SignInManager creates an identity with IdentityConstants.ApplicationScheme,
		// but OpenIddict only processes claims from identities with
		// TokenValidationParameters.DefaultAuthenticationType.
		var identity = new ClaimsIdentity(
			authenticationType: TokenValidationParameters.DefaultAuthenticationType,
			nameType: Claims.Name,
			roleType: Claims.Role);

		// Set the mandatory subject claim required by OpenIddict
		identity.SetClaim(Claims.Subject, user.Id.ToString());
		var principal = new ClaimsPrincipal(identity);

		// Set the requested scopes.
		// For authorization code / refresh token exchange, scopes come from the original
		// authorization principal (scopeOverrides), not from the token exchange request.
		var scopes = scopeOverrides ?? request.GetScopes();
		principal.SetScopes(scopes);

		// Set the resources based on the requested scopes.
		// Include the requesting client's client_id as a resource so it can
		// introspect its own tokens and receive the full set of claims.
		var resources = await _scopeManager.ListResourcesAsync(principal.GetScopes()).ToListAsync();
		if (!string.IsNullOrEmpty(request.ClientId) && !resources.Contains(request.ClientId))
		{
			resources.Add(request.ClientId);
		}
		principal.SetResources(resources);

		// Add additional claims based on scopes
		if (principal.HasScope(Scopes.Email) && !string.IsNullOrEmpty(user.Email))
		{
			identity.SetClaim(Claims.Email, user.Email);
			identity.SetClaim(Claims.EmailVerified, user.EmailConfirmed.ToString().ToLowerInvariant());
		}

		if (principal.HasScope(Scopes.Profile))
		{
			identity.SetClaim(Claims.PreferredUsername, user.UserName);
			identity.SetClaim(Claims.Name, GetDisplayName(user));

			if (!string.IsNullOrEmpty(user.FirstName))
			{
				identity.SetClaim(Claims.GivenName, user.FirstName);
			}

			if (!string.IsNullOrEmpty(user.LastName))
			{
				identity.SetClaim(Claims.FamilyName, user.LastName);
			}
		}

		if (principal.HasScope(Scopes.Phone) && !string.IsNullOrEmpty(user.PhoneNumber))
		{
			identity.SetClaim(Claims.PhoneNumber, user.PhoneNumber);
			identity.SetClaim(Claims.PhoneNumberVerified, user.PhoneNumberConfirmed.ToString().ToLowerInvariant());
		}

		if (principal.HasScope(Scopes.Roles))
		{
			var roles = await GetUserRoleNamesAsync(user);
			foreach (var role in roles)
			{
				identity.AddClaim(new Claim(Claims.Role, role));
			}
		}

		// Add custom user claims based on scope and API UserClaims configuration.
		// Collect the set of claim types that should be included based on:
		// 1. Scope UserClaims - each scope can declare which claim types it needs
		// 2. API UserClaims - each API can declare which claim types it needs
		var allowedClaimTypes = await GetAllowedClaimTypesAsync(scopes);
		if (allowedClaimTypes.Count > 0 && user.Claims.Count > 0)
		{
			foreach (var userClaim in user.Claims)
			{
				if (allowedClaimTypes.Contains(userClaim.Type))
				{
					identity.AddClaim(new Claim(userClaim.Type, userClaim.Value));
				}
			}
		}

		// Set the destinations for each claim
		principal.SetDestinations(GetDestinations);

		return principal;
	}

	/// <summary>
	/// Gets the user's display name.
	/// </summary>
	private static string GetDisplayName(ApplicationUser user)
	{
		if (!string.IsNullOrEmpty(user.FirstName) || !string.IsNullOrEmpty(user.LastName))
		{
			return $"{user.FirstName} {user.LastName}".Trim();
		}

		return user.UserName;
	}

	/// <summary>
	/// Gets the user's role names.
	/// </summary>
	private async Task<IList<string>> GetUserRoleNamesAsync(ApplicationUser user)
	{
		var roleNames = new List<string>();

		foreach (var roleId in user.Roles)
		{
			var role = await _roleRepository.GetByIdAsync(roleId);
			if (role is not null && !string.IsNullOrEmpty(role.Name))
			{
				roleNames.Add(role.Name);
			}
		}

		return roleNames;
	}

	/// <summary>
	/// Collects the set of allowed claim types based on the requested scopes.
	/// This combines UserClaims from both scope definitions and their associated APIs.
	/// </summary>
	private async Task<HashSet<string>> GetAllowedClaimTypesAsync(IEnumerable<string> requestedScopes)
	{
		var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var scopeNames = requestedScopes.ToList();

		if (scopeNames.Count == 0)
		{
			return allowedTypes;
		}

		// Get scope definitions to find their UserClaims
		var scopeStates = await _querySession.Query<OAuthScopeState>()
			.Where(s => s.Name.IsOneOf(scopeNames) && !s.IsDeleted)
			.ToListAsync();

		foreach (var scope in scopeStates)
		{
			foreach (var claimType in scope.UserClaims)
			{
				allowedTypes.Add(claimType);
			}
		}

		// Get APIs that are associated with the requested scopes
		// APIs have a Scopes list; if any requested scope is in that list, include the resource's UserClaims
		var apis = await _querySession.Query<OAuthApiState>()
			.Where(r => !r.IsDeleted && r.Enabled)
			.ToListAsync();

		foreach (var api in apis)
		{
			if (api.Scopes.Any(s => scopeNames.Contains(s)))
			{
				foreach (var claimType in api.UserClaims)
				{
					allowedTypes.Add(claimType);
				}
			}
		}

		return allowedTypes;
	}

	/// <summary>
	/// Determines which destinations the claim should be included in.
	/// </summary>
	private static IEnumerable<string> GetDestinations(Claim claim)
	{
		switch (claim.Type)
		{
			case Claims.Name or Claims.PreferredUsername:
				yield return Destinations.AccessToken;

				if (claim.Subject?.HasScope(Scopes.Profile) == true)
				{
					yield return Destinations.IdentityToken;
				}

				yield break;

			case Claims.Email:
				yield return Destinations.AccessToken;

				if (claim.Subject?.HasScope(Scopes.Email) == true)
				{
					yield return Destinations.IdentityToken;
				}

				yield break;

			case Claims.Role:
				yield return Destinations.AccessToken;

				if (claim.Subject?.HasScope(Scopes.Roles) == true)
				{
					yield return Destinations.IdentityToken;
				}

				yield break;

			// Never include the security stamp in tokens
			case "AspNet.Identity.SecurityStamp":
				yield break;

			default:
				yield return Destinations.AccessToken;
				yield break;
		}
	}
}
