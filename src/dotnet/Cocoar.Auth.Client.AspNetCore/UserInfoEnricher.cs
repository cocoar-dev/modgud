using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cocoar.Auth.Client.AspNetCore;

/// <summary>
/// Wires <c>JwtBearerEvents.OnTokenValidated</c> to fetch
/// <c>{Authority}/connect/userinfo</c> with the user's bearer token and
/// merge the <c>resource_access</c> claim onto the validated principal.
///
/// <para>Pure <c>AddJwtBearer</c> only validates the token — it doesn't
/// fetch UserInfo (that's an <c>AddOpenIdConnect</c> feature). For
/// resource servers that want the lib's claims-transformation to work,
/// the <c>resource_access</c> claim must reach the principal somehow.
/// This handler is the missing piece.</para>
///
/// <para>Network fault tolerance: if UserInfo is unreachable or returns
/// a non-2xx, the handler logs and silently continues — the request
/// proceeds with whatever claims the bearer token already carried, and
/// downstream gates (RequiresCocoarPermission, [Authorize(Roles=...)])
/// will return 403 if those weren't enough. This is the security-positive
/// default: a transient IdP outage MUST NOT 500 the whole API.</para>
/// </summary>
internal sealed class CocoarAuthUserInfoEnricher
{
    private static readonly HttpClient SharedClient = new();

    public static async Task EnrichAsync(TokenValidatedContext context)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<CocoarAuthOptions>>().Value;
        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("CocoarAuth.UserInfoEnricher");

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
                    "Cocoar.Auth: UserInfo fetch returned {Status}; continuing without enrichment.",
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
                    CocoarAuthClaimsTransformation.ResourceAccessClaimType,
                    resourceAccess.GetRawText()));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex,
                "Cocoar.Auth: UserInfo fetch failed; continuing without enrichment. " +
                "Downstream gates will use whatever the bearer token carried.");
        }
    }
}

/// <summary>
/// Hooks <see cref="CocoarAuthUserInfoEnricher.EnrichAsync"/> into the
/// configured JwtBearer scheme via PostConfigure. Composable: the host
/// can stack additional <c>OnTokenValidated</c> handlers, this one runs
/// first and just adds a claim.
/// </summary>
internal sealed class CocoarAuthJwtBearerPostConfigure : IPostConfigureOptions<JwtBearerOptions>
{
    private readonly CocoarAuthOptions _options;

    public CocoarAuthJwtBearerPostConfigure(IOptions<CocoarAuthOptions> options)
    {
        _options = options.Value;
    }

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (!string.Equals(name, _options.JwtBearerScheme, StringComparison.Ordinal)) return;

        if (string.IsNullOrWhiteSpace(_options.Authority))
            throw new InvalidOperationException(
                "CocoarAuthOptions.Authority must be set to the IdP base URL " +
                "(e.g. \"https://auth.cocoar.dev\") so the lib can fetch /connect/userinfo. " +
                "Configure it via AddCocoarAuthClient.");

        var existing = options.Events?.OnTokenValidated;
        options.Events ??= new JwtBearerEvents();
        options.Events.OnTokenValidated = async ctx =>
        {
            if (existing is not null) await existing(ctx);
            await CocoarAuthUserInfoEnricher.EnrichAsync(ctx);
        };
    }
}
