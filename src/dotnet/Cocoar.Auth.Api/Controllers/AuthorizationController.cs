using System.Security.Claims;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

	public AuthorizationController(
		IOpenIddictApplicationManager applicationManager,
		IOpenIddictAuthorizationManager authorizationManager,
		IOpenIddictScopeManager scopeManager,
		SignInManager<ApplicationUser> signInManager,
		UserManager<ApplicationUser> userManager,
		IRoleRepository roleRepository)
	{
		_applicationManager = applicationManager;
		_authorizationManager = authorizationManager;
		_scopeManager = scopeManager;
		_signInManager = signInManager;
		_userManager = userManager;
		_roleRepository = roleRepository;
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
		var result = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

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
				authenticationSchemes: CookieAuthenticationDefaults.AuthenticationScheme,
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
				authenticationSchemes: CookieAuthenticationDefaults.AuthenticationScheme,
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

		// For first-party IdP, auto-approve explicit consent
		// In a production system with third-party apps, you'd redirect to a consent page
		var consentPrincipal = await CreateClaimsPrincipalAsync(user, request);

		var consentAuthorization = await _authorizationManager.CreateAsync(
			principal: consentPrincipal,
			subject: await _userManager.GetUserIdAsync(user),
			client: await _applicationManager.GetIdAsync(application) ?? string.Empty,
			type: AuthorizationTypes.Permanent,
			scopes: consentPrincipal.GetScopes());

		consentPrincipal.SetAuthorizationId(await _authorizationManager.GetIdAsync(consentAuthorization));

		return SignIn(consentPrincipal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
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

			var principal = await CreateClaimsPrincipalAsync(user, request);

			// Set the existing authorization id
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
			identity.SetResources(await _scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync());

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

		return Ok(claims);
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
		OpenIddictRequest request)
	{
		var principal = await _signInManager.CreateUserPrincipalAsync(user);
		var identity = (ClaimsIdentity)principal.Identity!;

		// Set the requested scopes
		var scopes = request.GetScopes();
		principal.SetScopes(scopes);

		// Set the resources based on the requested scopes
		var resources = await _scopeManager.ListResourcesAsync(scopes).ToListAsync();
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
