using Modgud.Application.Services;
using Modgud.Authorization.Apps;
using Modgud.Domain.OAuth.Apis;
using Marten;

namespace Modgud.Api.Features.Auth;

/// <summary>
/// Endpoint filter that resolves an authenticated Resource-Server identity
/// from request headers and stamps it on
/// <see cref="HttpContext.Items"/> for downstream endpoints to read. Layered
/// alongside the existing user-authentication (cookie or bearer): the user
/// answers "who", the RS-auth answers "which resource-server is calling".
///
/// <para>Headers (case-insensitive):</para>
/// <list type="bullet">
///   <item><c>X-Resource-Server-Id</c> — the OAuthApi name (e.g.
///         <c>"timetodo-api"</c>)</item>
///   <item><c>X-Resource-Server-Secret</c> — the API secret in cleartext</item>
/// </list>
///
/// <para>Behaviour:</para>
/// <list type="bullet">
///   <item>Both headers absent → no-op. The endpoint behind continues with
///         user-only auth (status quo).</item>
///   <item>Exactly one header present → 401 with WWW-Authenticate. Sending
///         half a credential is a misconfig, never on purpose.</item>
///   <item>Both present, validation fails → 401.</item>
///   <item>Both present, validation succeeds → resolves the App via
///         <see cref="OAuthApiState.AppId"/> and stores
///         <see cref="ResourceServerContext"/> on
///         <see cref="HttpContext.Items"/>.</item>
/// </list>
/// </summary>
public sealed class ResourceServerAuthFilter : IEndpointFilter
{
    private const string IdHeader = "X-Resource-Server-Id";
    private const string SecretHeader = "X-Resource-Server-Secret";

    public const string ContextItemKey = "Modgud.ResourceServer";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var idRaw = http.Request.Headers[IdHeader].ToString();
        var secret = http.Request.Headers[SecretHeader].ToString();
        var hasId = !string.IsNullOrEmpty(idRaw);
        var hasSecret = !string.IsNullOrEmpty(secret);

        // Both absent → user-only auth path. The endpoint decides what to do
        // (cookie + ?app=, or bearer-derived single-app).
        if (!hasId && !hasSecret) return await next(context);

        // Half-supplied credential → misconfig. Never silently fall through;
        // the operator deserves to know their integration is broken.
        if (hasId != hasSecret)
        {
            return UnauthorizedRsAuth(http,
                "Both X-Resource-Server-Id and X-Resource-Server-Secret must be supplied together.");
        }

        var session = http.RequestServices.GetRequiredService<IDocumentSession>();
        var oauthAdmin = http.RequestServices.GetRequiredService<OAuthAdminService>();

        var api = await session.Query<OAuthApiState>()
            .FirstOrDefaultAsync(a => a.Name == idRaw && !a.IsDeleted);
        if (api is null || !api.Enabled)
        {
            return UnauthorizedRsAuth(http, "Unknown or disabled resource server.");
        }

        var ok = await oauthAdmin.ValidateApiCredentialsAsync(api.Id.ToString(), secret);
        if (!ok)
        {
            return UnauthorizedRsAuth(http, "Resource-server credentials rejected.");
        }

        // Resolve the App so downstream endpoints get the slug for free.
        // An RS without an AppId is technically authenticated but cannot
        // fulfil app-scoped requests — mark that explicitly so endpoints
        // can reject with a clear error rather than silently fall through.
        App? app = null;
        if (api.AppId is Guid appId)
        {
            app = await session.LoadAsync<App>(appId);
            if (app?.IsDeleted == true) app = null;
        }

        http.Items[ContextItemKey] = new ResourceServerContext(
            ApiId: api.Id,
            ApiName: api.Name,
            App: app);

        return await next(context);
    }

    private static IResult UnauthorizedRsAuth(HttpContext http, string description)
    {
        // RFC 6750 surface — clients that look for WWW-Authenticate get a
        // pointer at what failed. The realm here is descriptive; the actual
        // cookie/bearer auth uses its own challenge separately.
        http.Response.Headers.WWWAuthenticate =
            $"ModgudRS error=\"invalid_client\", error_description=\"{description}\"";
        return Results.Unauthorized();
    }
}

/// <summary>
/// Resolved resource-server context for a request that authenticated via
/// the RS-Auth filter. Stored on <see cref="HttpContext.Items"/> under
/// <see cref="ResourceServerAuthFilter.ContextItemKey"/>.
/// </summary>
public sealed record ResourceServerContext(Guid ApiId, string ApiName, App? App);
