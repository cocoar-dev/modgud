using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modgud.AspNetCore.ResourceServer;

internal sealed class ModgudIntrospectionHandler : AuthenticationHandler<ModgudIntrospectionOptions>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ModgudIntrospectionHandler(
        IOptionsMonitor<ModgudIntrospectionOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IHttpClientFactory httpClientFactory)
        : base(options, logger, encoder)
    {
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawAuthorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(rawAuthorization) ||
            !AuthenticationHeaderValue.TryParse(rawAuthorization, out var header) ||
            string.IsNullOrEmpty(header.Parameter))
        {
            return AuthenticateResult.NoResult();
        }

        var isBearer = string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase);
        var isDpop = string.Equals(header.Scheme, Dpop.DpopResource.Scheme, StringComparison.OrdinalIgnoreCase);
        if (!isBearer && !isDpop)
            return AuthenticateResult.NoResult();

        var client = _httpClientFactory.CreateClient(ModgudHttpClientNames.Introspection);
        var principal = await ModgudTokenIntrospection.IntrospectAsync(
            client,
            Options,
            header.Parameter,
            Scheme.Name,
            Logger,
            Context.RequestAborted);
        if (principal is null)
            return AuthenticateResult.Fail("Modgud introspection did not affirmatively validate the token.");

        var boundJkt = principal.FindFirst(Dpop.DpopResource.ConfirmationJktClaimType)?.Value;
        if (isDpop)
        {
            if (string.IsNullOrEmpty(boundJkt))
                return AuthenticateResult.Fail("The DPoP scheme was used but the token is not DPoP-bound.");

            var outcome = Dpop.DpopResourceValidator.Validate(
                Request,
                header.Parameter,
                boundJkt,
                DateTimeOffset.UtcNow);
            if (outcome != Dpop.DpopResourceResult.Valid)
                return AuthenticateResult.Fail($"The DPoP proof did not validate ({outcome}).");
        }
        else if (!string.IsNullOrEmpty(boundJkt))
        {
            return AuthenticateResult.Fail(
                "This access token is DPoP-bound and must be presented with the DPoP scheme.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

internal static class ModgudTokenIntrospection
{
    public static async Task<ClaimsPrincipal?> IntrospectAsync(
        HttpClient client,
        ModgudIntrospectionOptions options,
        string token,
        string authenticationType,
        ILogger logger,
        CancellationToken ct)
    {
        var url = options.Authority.TrimEnd('/') + "/connect/introspect";
        using var content = new FormUrlEncodedContent(
        [
            new("token", token),
            new("token_type_hint", "access_token"),
            new("client_id", options.ClientId),
            new("client_secret", options.ClientSecret),
        ]);

        string body;
        try
        {
            using var response = await client.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "Modgud: /connect/introspect returned {Status}; rejecting the token.",
                    (int)response.StatusCode);
                return null;
            }

            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Modgud: /connect/introspect call failed; rejecting the token.");
            return null;
        }

        return BuildPrincipal(body, options.Audience, authenticationType, logger);
    }

    internal static ClaimsPrincipal? BuildPrincipal(
        string introspectionBody,
        string audience,
        string authenticationType,
        ILogger logger)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(introspectionBody);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Modgud: /connect/introspect returned unparseable JSON; rejecting the token.");
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("active", out var active) ||
            active.ValueKind != JsonValueKind.True ||
            !AudienceContains(root, audience))
        {
            return null;
        }

        var identity = new ClaimsIdentity(
            authenticationType,
            nameType: "name",
            roleType: ClaimTypes.Role);

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "resource_access" when property.Value.ValueKind == JsonValueKind.Object:
                    identity.AddClaim(new Claim(
                        ModgudClaimTypes.ResourceAccess,
                        property.Value.GetRawText(),
                        Microsoft.IdentityModel.JsonWebTokens.JsonClaimValueTypes.Json));
                    break;

                case "cnf" when property.Value.ValueKind == JsonValueKind.Object &&
                                property.Value.TryGetProperty("jkt", out var jkt) &&
                                jkt.ValueKind == JsonValueKind.String:
                    identity.AddClaim(new Claim(Dpop.DpopResource.ConfirmationJktClaimType, jkt.GetString()!));
                    break;

                case "sub" when property.Value.ValueKind == JsonValueKind.String:
                    identity.AddClaim(new Claim("sub", property.Value.GetString()!));
                    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, property.Value.GetString()!));
                    break;

                case "name" or "preferred_username" or "email" or "scope" or "client_id"
                    when property.Value.ValueKind == JsonValueKind.String:
                    identity.AddClaim(new Claim(property.Name, property.Value.GetString()!));
                    break;
            }
        }

        var principal = new ClaimsPrincipal(identity);
        ModgudClaimsProjector.Project(principal, audience);
        return principal;
    }

    private static bool AudienceContains(JsonElement root, string audience)
    {
        if (!root.TryGetProperty("aud", out var audiences)) return false;
        return audiences.ValueKind switch
        {
            JsonValueKind.String => string.Equals(audiences.GetString(), audience, StringComparison.Ordinal),
            JsonValueKind.Array => audiences.EnumerateArray().Any(
                item => item.ValueKind == JsonValueKind.String &&
                        string.Equals(item.GetString(), audience, StringComparison.Ordinal)),
            _ => false,
        };
    }
}
