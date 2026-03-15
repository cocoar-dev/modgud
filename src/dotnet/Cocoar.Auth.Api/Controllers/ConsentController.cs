using System.Collections.Immutable;
using System.Security.Claims;
using Cocoar.Auth.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Api.Controllers;

/// <summary>
/// Handles user consent for OAuth 2.0 / OpenID Connect authorization requests.
/// </summary>
[Route("api/[controller]")]
[Authorize]
public class ConsentController : ApiControllerBase
{
	private readonly IOpenIddictApplicationManager _applicationManager;
	private readonly IOpenIddictAuthorizationManager _authorizationManager;
	private readonly IOpenIddictScopeManager _scopeManager;
	private readonly UserManager<ApplicationUser> _userManager;

	public ConsentController(
		IOpenIddictApplicationManager applicationManager,
		IOpenIddictAuthorizationManager authorizationManager,
		IOpenIddictScopeManager scopeManager,
		UserManager<ApplicationUser> userManager)
	{
		_applicationManager = applicationManager;
		_authorizationManager = authorizationManager;
		_scopeManager = scopeManager;
		_userManager = userManager;
	}

	/// <summary>
	/// Returns the consent model for the current authorization request.
	/// </summary>
	[HttpGet]
	public async Task<IActionResult> GetConsentInfo([FromQuery] string returnUrl)
	{
		if (string.IsNullOrEmpty(returnUrl))
		{
			return BadRequest(new { message = "The returnUrl parameter is required." });
		}

		// Parse the returnUrl to extract the authorization request parameters
		var (clientId, scopes) = ParseAuthorizationUrl(returnUrl);

		if (string.IsNullOrEmpty(clientId))
		{
			return BadRequest(new { message = "Invalid authorization request." });
		}

		// Find the application
		var application = await _applicationManager.FindByClientIdAsync(clientId);
		if (application is null)
		{
			return NotFound(new { message = "Application not found." });
		}

		var clientName = await _applicationManager.GetDisplayNameAsync(application) ?? clientId;

		// Build the scope information
		var scopeInfos = new List<ConsentScopeInfo>();
		foreach (var scopeName in scopes)
		{
			var scope = await _scopeManager.FindByNameAsync(scopeName);
			string? displayName = null;
			string? description = null;

			if (scope is not null)
			{
				displayName = await _scopeManager.GetDisplayNameAsync(scope);
				description = await _scopeManager.GetDescriptionAsync(scope);
			}

			// openid scope is always required
			var isRequired = scopeName == Scopes.OpenId;

			scopeInfos.Add(new ConsentScopeInfo
			{
				Name = scopeName,
				DisplayName = displayName ?? scopeName,
				Description = description,
				Required = isRequired
			});
		}

		return Ok(new ConsentModel
		{
			ClientId = clientId,
			ClientName = clientName,
			RequestedScopes = scopeInfos,
			ReturnUrl = returnUrl
		});
	}

	/// <summary>
	/// Processes the user's consent decision.
	/// </summary>
	[HttpPost]
	public async Task<IActionResult> SubmitConsent([FromBody] ConsentDecision decision)
	{
		if (string.IsNullOrEmpty(decision.ReturnUrl))
		{
			return BadRequest(new { message = "The returnUrl is required." });
		}

		var (clientId, requestedScopes) = ParseAuthorizationUrl(decision.ReturnUrl);

		if (string.IsNullOrEmpty(clientId))
		{
			return BadRequest(new { message = "Invalid authorization request." });
		}

		// If the user denied consent, redirect with an error
		if (!decision.Approved)
		{
			return Ok(new ConsentResult
			{
				RedirectUrl = AppendErrorToUrl(
					decision.ReturnUrl,
					Errors.AccessDenied,
					"The user denied the authorization request.")
			});
		}

		// Validate that required scopes are included
		if (!decision.ApprovedScopes.Contains(Scopes.OpenId) && requestedScopes.Contains(Scopes.OpenId))
		{
			decision.ApprovedScopes.Add(Scopes.OpenId);
		}

		// Find the application
		var application = await _applicationManager.FindByClientIdAsync(clientId);
		if (application is null)
		{
			return NotFound(new { message = "Application not found." });
		}

		// Get the current user
		var user = await _userManager.GetUserAsync(User);
		if (user is null)
		{
			return Unauthorized(new { message = "User not found." });
		}

		// Store the consent grant as a permanent authorization
		// The scopes are stored in the authorization so the next time the same client
		// requests the same scopes, the user won't be prompted again
		var identity = new ClaimsIdentity(
			authenticationType: TokenValidationParameters.DefaultAuthenticationType,
			nameType: Claims.Name,
			roleType: Claims.Role);

		identity.SetClaim(Claims.Subject, user.Id.ToString());

		var principal = new ClaimsPrincipal(identity);
		principal.SetScopes(decision.ApprovedScopes);

		var authorization = await _authorizationManager.CreateAsync(
			principal: principal,
			subject: await _userManager.GetUserIdAsync(user),
			client: await _applicationManager.GetIdAsync(application) ?? string.Empty,
			type: AuthorizationTypes.Permanent,
			scopes: decision.ApprovedScopes.ToImmutableArray());

		// Return the original authorize URL so the frontend can redirect back
		// The authorization endpoint will now find the permanent authorization and proceed
		return Ok(new ConsentResult
		{
			RedirectUrl = decision.ReturnUrl
		});
	}

	/// <summary>
	/// Parses a /connect/authorize URL to extract client_id and scope parameters.
	/// </summary>
	private static (string? clientId, List<string> scopes) ParseAuthorizationUrl(string url)
	{
		try
		{
			// The URL may be a relative path with query string
			Uri uri;
			if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
			{
				uri = new Uri(url);
			}
			else
			{
				uri = new Uri("http://localhost" + url);
			}

			var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);

			string? clientId = null;
			var scopes = new List<string>();

			if (query.TryGetValue("client_id", out var clientIdValues))
			{
				clientId = clientIdValues.FirstOrDefault();
			}

			if (query.TryGetValue("scope", out var scopeValues))
			{
				var scopeString = scopeValues.FirstOrDefault();
				if (!string.IsNullOrEmpty(scopeString))
				{
					scopes = scopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
				}
			}

			return (clientId, scopes);
		}
		catch
		{
			return (null, new List<string>());
		}
	}

	/// <summary>
	/// Appends error parameters to a URL for error responses.
	/// </summary>
	private static string AppendErrorToUrl(string url, string error, string description)
	{
		// For denied consent, we need to tell the frontend to show an error
		// rather than redirect back to the authorize endpoint
		return $"/consent/denied?error={Uri.EscapeDataString(error)}&error_description={Uri.EscapeDataString(description)}";
	}
}

/// <summary>
/// Model representing the consent information shown to the user.
/// </summary>
public class ConsentModel
{
	public required string ClientId { get; init; }
	public required string ClientName { get; init; }
	public required List<ConsentScopeInfo> RequestedScopes { get; init; }
	public required string ReturnUrl { get; init; }
}

/// <summary>
/// Information about a scope shown on the consent screen.
/// </summary>
public class ConsentScopeInfo
{
	public required string Name { get; init; }
	public required string DisplayName { get; init; }
	public string? Description { get; init; }
	public bool Required { get; init; }
}

/// <summary>
/// The user's consent decision.
/// </summary>
public class ConsentDecision
{
	public bool Approved { get; init; }
	public List<string> ApprovedScopes { get; init; } = new();
	public required string ReturnUrl { get; init; }
}

/// <summary>
/// The result of a consent decision.
/// </summary>
public class ConsentResult
{
	public required string RedirectUrl { get; init; }
}
