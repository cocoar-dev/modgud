using System.Collections.Immutable;
using System.Security.Claims;
using Cocoar.Auth.Authentication.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Api.Features.Auth.OAuth;

/// <summary>
/// Minimal-API endpoints for the consent UI flow. Matches the SPA contract from the
/// legacy backend: GET returns the consent model, POST accepts the user decision and
/// either creates a permanent authorization or denies.
/// </summary>
public static class ConsentEndpoints
{
    public static WebApplication MapConsentEndpoints(this WebApplication app, string pathBase = "connect")
    {
        var group = app.MapGroup($"~/{pathBase}/consent")
            .WithTags("OpenIddict")
            .RequireAuthorization();

        group.MapGet("", GetConsentInfoAsync).WithName("OAuth_Consent_Get");
        group.MapPost("", SubmitConsentAsync).WithName("OAuth_Consent_Post");

        return app;
    }

    private static async Task<IResult> GetConsentInfoAsync(
        string returnUrl,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager)
    {
        if (string.IsNullOrEmpty(returnUrl)) return Results.BadRequest(new { message = "The returnUrl parameter is required." });

        var (clientId, scopes) = ParseAuthorizationUrl(returnUrl);
        if (string.IsNullOrEmpty(clientId)) return Results.BadRequest(new { message = "Invalid authorization request." });

        var application = await applicationManager.FindByClientIdAsync(clientId);
        if (application is null) return Results.NotFound(new { message = "Application not found." });

        var clientName = await applicationManager.GetDisplayNameAsync(application) ?? clientId;

        var scopeInfos = new List<ConsentScopeInfo>();
        foreach (var scopeName in scopes)
        {
            var scope = await scopeManager.FindByNameAsync(scopeName);
            string? displayName = null;
            string? description = null;
            if (scope is not null)
            {
                displayName = await scopeManager.GetDisplayNameAsync(scope);
                description = await scopeManager.GetDescriptionAsync(scope);
            }
            scopeInfos.Add(new ConsentScopeInfo
            {
                Name = scopeName,
                DisplayName = displayName ?? scopeName,
                Description = description,
                Required = scopeName == Scopes.OpenId,
            });
        }

        return Results.Ok(new ConsentModel
        {
            ClientId = clientId,
            ClientName = clientName,
            RequestedScopes = scopeInfos,
            ReturnUrl = returnUrl,
        });
    }

    private static async Task<IResult> SubmitConsentAsync(
        ConsentDecision decision,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        UserManager<ApplicationUser> userManager,
        ClaimsPrincipal currentUserPrincipal)
    {
        if (string.IsNullOrEmpty(decision.ReturnUrl)) return Results.BadRequest(new { message = "The returnUrl is required." });

        var (clientId, requestedScopes) = ParseAuthorizationUrl(decision.ReturnUrl);
        if (string.IsNullOrEmpty(clientId)) return Results.BadRequest(new { message = "Invalid authorization request." });

        if (!decision.Approved)
        {
            return Results.Ok(new ConsentResult
            {
                RedirectUrl = AppendErrorToUrl(decision.ReturnUrl, Errors.AccessDenied, "The user denied the authorization request."),
            });
        }

        if (!decision.ApprovedScopes.Contains(Scopes.OpenId) && requestedScopes.Contains(Scopes.OpenId))
        {
            decision.ApprovedScopes.Add(Scopes.OpenId);
        }

        var application = await applicationManager.FindByClientIdAsync(clientId);
        if (application is null) return Results.NotFound(new { message = "Application not found." });

        var user = await userManager.GetUserAsync(currentUserPrincipal);
        if (user is null) return Results.Unauthorized();

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString());

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(decision.ApprovedScopes);

        await authorizationManager.CreateAsync(
            principal: principal,
            subject: await userManager.GetUserIdAsync(user),
            client: await applicationManager.GetIdAsync(application) ?? string.Empty,
            type: AuthorizationTypes.Permanent,
            scopes: decision.ApprovedScopes.ToImmutableArray());

        return Results.Ok(new ConsentResult { RedirectUrl = decision.ReturnUrl });
    }

    private static (string? clientId, List<string> scopes) ParseAuthorizationUrl(string url)
    {
        try
        {
            var uri = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new Uri(url)
                : new Uri("http://localhost" + url);

            var query = QueryHelpers.ParseQuery(uri.Query);

            string? clientId = query.TryGetValue("client_id", out var clientIdValues) ? clientIdValues.FirstOrDefault() : null;
            var scopes = new List<string>();
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

    private static string AppendErrorToUrl(string url, string error, string description)
        => $"/consent/denied?error={Uri.EscapeDataString(error)}&error_description={Uri.EscapeDataString(description)}";
}

public class ConsentModel
{
    public required string ClientId { get; init; }
    public required string ClientName { get; init; }
    public required List<ConsentScopeInfo> RequestedScopes { get; init; }
    public required string ReturnUrl { get; init; }
}

public class ConsentScopeInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
}

public class ConsentDecision
{
    public bool Approved { get; init; }
    public List<string> ApprovedScopes { get; init; } = new();
    public required string ReturnUrl { get; init; }
}

public class ConsentResult
{
    public required string RedirectUrl { get; init; }
}
