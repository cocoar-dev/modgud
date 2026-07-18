using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modgud.Client.AspNetCore;

/// <summary>
/// Validates opaque Modgud <b>reference</b> access tokens by calling the IdP's
/// <c>/connect/introspect</c> endpoint (RFC 7662) and projecting the response
/// onto a <see cref="ClaimsPrincipal"/> — including the per-audience
/// <c>resource_access</c> block, which the shared
/// <see cref="ModgudClaimsTransformation"/> then flattens into role /
/// permission claims exactly as for the JWT path.
///
/// <para><b>Fail-closed.</b> Unlike <see cref="ModgudUserInfoEnricher"/> (which
/// enriches an already-validated JWT and so fails open on a UserInfo outage),
/// introspection <em>is</em> the validation here. A non-2xx response, a
/// transport error, an <c>active:false</c> body, or an audience mismatch all
/// reject the request — a token that can't be affirmatively validated is not
/// honoured.</para>
/// </summary>
internal sealed class ModgudIntrospectionHandler : AuthenticationHandler<ModgudReferenceTokenOptions>
{
    public ModgudIntrospectionHandler(
        IOptionsMonitor<ModgudReferenceTokenOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawAuth = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(rawAuth) ||
            !AuthenticationHeaderValue.TryParse(rawAuth, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter))
        {
            // No bearer token → this handler has no opinion; the pipeline
            // treats the request as anonymous (a 401 challenge follows only
            // if the endpoint requires authorization).
            return AuthenticateResult.NoResult();
        }

        var principal = await ModgudTokenIntrospection.IntrospectAsync(
            Options, header.Parameter!, Scheme.Name, Logger, Context.RequestAborted);

        return principal is null
            ? AuthenticateResult.Fail("Modgud introspection did not affirmatively validate the token.")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

/// <summary>
/// The pure introspection + claims-projection logic behind
/// <see cref="ModgudIntrospectionHandler"/>, factored out so it can be unit
/// tested against a stub HTTP handler without standing up the auth pipeline.
/// </summary>
internal static class ModgudTokenIntrospection
{
    // Settable seam so unit tests can substitute a fake HttpMessageHandler and
    // assert on the introspection request. Production callers never touch it.
    internal static HttpClient SharedClient { get; set; } = new();

    /// <summary>
    /// Introspects <paramref name="token"/> and, if it is active and audience-valid,
    /// returns a principal carrying the introspection claims (including the raw
    /// <c>resource_access</c> claim the transformation reads). Returns
    /// <c>null</c> on any failure — the caller treats that as "reject".
    /// </summary>
    public static async Task<ClaimsPrincipal?> IntrospectAsync(
        ModgudReferenceTokenOptions options,
        string token,
        string authenticationType,
        ILogger logger,
        CancellationToken ct)
    {
        var url = options.Authority.TrimEnd('/') + "/connect/introspect";
        // Form-body client authentication (client_secret_post). A URL-shaped
        // client_id (the MCP audience case) collides with HTTP Basic, which
        // splits client_id:secret on the scheme colon.
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("token", token),
            new KeyValuePair<string, string>("token_type_hint", "access_token"),
            new KeyValuePair<string, string>("client_id", options.ResolvedClientId),
            new KeyValuePair<string, string>("client_secret", options.IntrospectionClientSecret ?? string.Empty),
        });

        string body;
        try
        {
            using var response = await SharedClient.PostAsync(url, content, ct);
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
            // Fail-closed: introspection is the validation, so an outage means
            // we cannot affirm the token — reject rather than admit it.
            logger.LogWarning(ex, "Modgud: /connect/introspect call failed; rejecting the token.");
            return null;
        }

        return BuildPrincipal(body, options.Audience, authenticationType, logger);
    }

    /// <summary>Projects an introspection response body onto a principal, or
    /// returns <c>null</c> when the token is inactive, malformed, or not for
    /// this audience.</summary>
    internal static ClaimsPrincipal? BuildPrincipal(
        string introspectionBody, string audience, string authenticationType, ILogger logger)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(introspectionBody);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Modgud: /connect/introspect returned unparseable JSON; rejecting the token.");
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("active", out var active) ||
            active.ValueKind != JsonValueKind.True)
        {
            // active:false (or missing) — RFC 7662 §2.2. Nothing else is trustworthy.
            return null;
        }

        // Defence in depth: only honour a token that names this RS in its aud.
        // (The IdP already gates this — it returns active:false to a caller that
        // isn't an audience/presenter — but a misconfigured introspection client
        // id must never let a foreign-audience token through.)
        if (!AudienceContains(root, audience))
        {
            logger.LogWarning(
                "Modgud: introspected token is active but its audience does not include '{Audience}'; rejecting.",
                audience);
            return null;
        }

        var identity = new ClaimsIdentity(
            authenticationType, nameType: "name", roleType: ClaimTypes.Role);

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                // The load-bearing claim: keep the raw JSON so
                // ModgudClaimsTransformation can flatten resource_access[audience].
                case "resource_access" when property.Value.ValueKind == JsonValueKind.Object:
                    identity.AddClaim(new Claim(
                        ModgudClaimsTransformation.ResourceAccessClaimType,
                        property.Value.GetRawText(),
                        Microsoft.IdentityModel.JsonWebTokens.JsonClaimValueTypes.Json));
                    break;

                // Standard string scalars worth surfacing on the principal.
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

        return new ClaimsPrincipal(identity);
    }

    private static bool AudienceContains(JsonElement root, string audience)
    {
        if (!root.TryGetProperty("aud", out var aud)) return false;
        return aud.ValueKind switch
        {
            JsonValueKind.String => string.Equals(aud.GetString(), audience, StringComparison.Ordinal),
            JsonValueKind.Array => aud.EnumerateArray().Any(
                e => e.ValueKind == JsonValueKind.String &&
                     string.Equals(e.GetString(), audience, StringComparison.Ordinal)),
            _ => false,
        };
    }
}
