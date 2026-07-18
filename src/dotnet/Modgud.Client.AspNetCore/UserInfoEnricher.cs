using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modgud.Client.AspNetCore;

/// <summary>
/// Wires <c>JwtBearerEvents.OnTokenValidated</c> to make sure the validated
/// principal carries a <c>resource_access</c> claim, preferring the token's
/// own embedded claim and falling back to
/// <c>{Authority}/connect/userinfo</c> only when the token carries none.
///
/// <para>Preference order and why: since federation v1.1 the IdP bakes
/// <c>resource_access</c> straight into every access token at issuance —
/// <c>/connect/userinfo</c> merely echoes that same baked block back
/// verbatim (see <c>UserInfoPerAudienceTests
/// .JwtClient_Bakes_ResourceAccess_Into_AccessToken_And_UserInfo_Echoes</c>
/// on the IdP side). So when the JwtBearer-validated token already has the
/// claim, fetching UserInfo is a redundant round-trip that returns the exact
/// same data — this handler skips it. It only falls back to UserInfo for
/// tokens that don't carry the claim themselves (e.g. opaque/reference
/// access tokens the host validates via introspection instead of JWT
/// parsing, or older IdP versions).</para>
///
/// <para>Pure <c>AddJwtBearer</c> only validates the token — it doesn't
/// fetch UserInfo on its own (that's an <c>AddOpenIdConnect</c> feature).
/// For resource servers that want the lib's claims-transformation to work
/// even when the token itself has no <c>resource_access</c>, the claim must
/// reach the principal somehow. This handler is the missing piece.</para>
///
/// <para>Network fault tolerance: if UserInfo is unreachable or returns
/// a non-2xx, the handler logs and silently continues — the request
/// proceeds with whatever claims the bearer token already carried, and
/// downstream gates (RequiresModgudPermission, [Authorize(Roles=...)])
/// will return 403 if those weren't enough. This is the security-positive
/// default: a transient IdP outage MUST NOT 500 the whole API. Note this
/// fail-open behaviour only applies to the fallback path — a token that
/// already carries the claim never touches the network at all.</para>
/// </summary>
internal sealed class ModgudUserInfoEnricher
{
    // Settable (not just a readonly field) so unit tests can substitute a
    // fake HttpMessageHandler and assert on call counts. Production callers
    // never touch this — it defaults to a real HttpClient.
    internal static HttpClient SharedClient { get; set; } = new();

    public static async Task EnrichAsync(TokenValidatedContext context)
    {
        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Modgud.UserInfoEnricher");

        // Preference order: the validated token's own resource_access claim
        // wins. /connect/userinfo only ever echoes the same baked block, so
        // if it's already on the principal there is nothing UserInfo could
        // add — skip the round-trip entirely.
        if (context.Principal?.Identity is ClaimsIdentity validatedIdentity &&
            !string.IsNullOrEmpty(validatedIdentity
                .FindFirst(ModgudClaimsTransformation.ResourceAccessClaimType)?.Value))
        {
            logger.LogDebug(
                "Modgud: access token already carries a resource_access claim; " +
                "skipping the /connect/userinfo round-trip.");
            return;
        }

        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<ModgudOptions>>().Value;

        // Token was just validated → the bearer string is on the request.
        var rawAuth = context.HttpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(rawAuth) ||
            !AuthenticationHeaderValue.TryParse(rawAuth, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter))
        {
            return;
        }

        var url = options.Authority.TrimEnd('/') + "/connect/userinfo";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", header.Parameter);

        try
        {
            using var response = await SharedClient.SendAsync(request,
                context.HttpContext.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "Modgud: UserInfo fetch returned {Status}; continuing without enrichment.",
                    (int)response.StatusCode);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted);
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("resource_access", out var resourceAccess) ||
                resourceAccess.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // Add as a string-typed claim — the ClaimsTransformation will
            // parse the JSON and project the configured-audience block.
            if (context.Principal?.Identity is ClaimsIdentity identity)
            {
                identity.AddClaim(new Claim(
                    ModgudClaimsTransformation.ResourceAccessClaimType,
                    resourceAccess.GetRawText()));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex,
                "Modgud: UserInfo fetch failed; continuing without enrichment. " +
                "Downstream gates will use whatever the bearer token carried.");
        }
    }
}

/// <summary>
/// Hooks <see cref="ModgudUserInfoEnricher.EnrichAsync"/> into the
/// configured JwtBearer scheme via PostConfigure. Composable: the host
/// can stack additional <c>OnTokenValidated</c> handlers, this one runs
/// first and either confirms the token already carries
/// <c>resource_access</c> or adds the claim from UserInfo as a fallback.
/// </summary>
internal sealed class ModgudJwtBearerPostConfigure : IPostConfigureOptions<JwtBearerOptions>
{
    private readonly ModgudOptions _options;

    public ModgudJwtBearerPostConfigure(IOptions<ModgudOptions> options)
    {
        _options = options.Value;
    }

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (!string.Equals(name, _options.JwtBearerScheme, StringComparison.Ordinal)) return;

        if (string.IsNullOrWhiteSpace(_options.Authority))
            throw new InvalidOperationException(
                "ModgudOptions.Authority must be set to the IdP base URL " +
                "(e.g. \"https://auth.example.com\") so the lib can fetch /connect/userinfo. " +
                "Configure it via AddModgudClient.");

        var existing = options.Events?.OnTokenValidated;
        options.Events ??= new JwtBearerEvents();
        options.Events.OnTokenValidated = async ctx =>
        {
            if (existing is not null) await existing(ctx);
            await ModgudUserInfoEnricher.EnrichAsync(ctx);
        };
    }
}
