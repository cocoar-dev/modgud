using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Text.Json;
using Cocoar.Auth.Application.Interfaces;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Implementation of OIDC protocol operations using Microsoft.IdentityModel.
/// </summary>
public class OidcProtocolService : IOidcProtocolService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _configManagers = new();

    public OidcProtocolService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> BuildAuthorizationUrlAsync(
        OidcProviderConfig config,
        string redirectUri,
        string state,
        string nonce,
        string codeChallenge,
        CancellationToken cancellationToken = default)
    {
        var oidcConfig = await GetOpenIdConnectConfigurationAsync(config.Authority, cancellationToken);

        var scopes = config.Scopes ?? "openid profile email";

        var authUrl = $"{oidcConfig.AuthorizationEndpoint}" +
            $"?response_type=code" +
            $"&client_id={Uri.EscapeDataString(config.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scopes)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&nonce={Uri.EscapeDataString(nonce)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256";

        return authUrl;
    }

    public async Task<OidcTokenResponse?> ExchangeCodeAsync(
        OidcProviderConfig config,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        var oidcConfig = await GetOpenIdConnectConfigurationAsync(config.Authority, cancellationToken);
        var httpClient = _httpClientFactory.CreateClient();

        var tokenRequest = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["code_verifier"] = codeVerifier
        };

        var request = new HttpRequestMessage(HttpMethod.Post, oidcConfig.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(tokenRequest)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResponse = JsonDocument.Parse(json);

        var idToken = tokenResponse.RootElement.GetProperty("id_token").GetString();
        if (idToken is null)
        {
            return null;
        }

        var accessToken = tokenResponse.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        var refreshToken = tokenResponse.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        return new OidcTokenResponse(idToken, accessToken, refreshToken);
    }

    public async Task<OidcUserInfo?> ValidateIdTokenAsync(
        OidcProviderConfig config,
        string idToken,
        string expectedNonce,
        CancellationToken cancellationToken = default)
    {
        var oidcConfig = await GetOpenIdConnectConfigurationAsync(config.Authority, cancellationToken);

        var validationParameters = new TokenValidationParameters
        {
            ValidIssuer = oidcConfig.Issuer,
            ValidAudience = config.ClientId,
            IssuerSigningKeys = oidcConfig.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(5)
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(idToken, validationParameters, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwt)
            {
                return null;
            }

            // Validate nonce
            var nonceClaim = jwt.Claims.FirstOrDefault(c => c.Type == "nonce")?.Value;
            if (nonceClaim != expectedNonce)
            {
                return null;
            }

            var subject = jwt.Subject;
            if (string.IsNullOrEmpty(subject))
            {
                return null;
            }

            var email = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
            var emailVerified = jwt.Claims.FirstOrDefault(c => c.Type == "email_verified")?.Value == "true"
                || jwt.Claims.FirstOrDefault(c => c.Type == "email_verified")?.Value == "True";
            var name = jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
            var givenName = jwt.Claims.FirstOrDefault(c => c.Type == "given_name")?.Value;
            var familyName = jwt.Claims.FirstOrDefault(c => c.Type == "family_name")?.Value;
            var preferredUsername = jwt.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value;

            return new OidcUserInfo(subject, email, emailVerified, name, givenName, familyName, preferredUsername);
        }
        catch
        {
            return null;
        }
    }

    private async Task<OpenIdConnectConfiguration> GetOpenIdConnectConfigurationAsync(
        string authority,
        CancellationToken cancellationToken)
    {
        var configManager = _configManagers.GetOrAdd(authority, key =>
        {
            var metadataAddress = key.TrimEnd('/') + "/.well-known/openid-configuration";
            var retriever = new HttpDocumentRetriever(_httpClientFactory.CreateClient())
            {
                RequireHttps = metadataAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            };
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                retriever);
        });

        return await configManager.GetConfigurationAsync(cancellationToken);
    }
}
