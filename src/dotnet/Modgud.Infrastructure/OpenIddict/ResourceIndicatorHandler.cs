using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Implements RFC 8707 (Resource Indicators for OAuth 2.0).
///
/// <para>When a client includes one or more <c>resource=…</c> parameters in
/// the authorize and/or token request, this handler:
/// </para>
/// <list type="bullet">
///   <item><description>Validates that each requested resource is one the
///   principal would otherwise have been granted via scope-binding or
///   client_id fallback (i.e., resources already present in
///   <c>principal.GetResources()</c> after
///   <c>AuthorizationEndpoints.CreateClaimsPrincipalAsync</c> ran).</description></item>
///   <item><description>If any requested resource is not authorised: rejects
///   the sign-in with the RFC-8707 <c>invalid_target</c> error. The token
///   is not issued.</description></item>
///   <item><description>If all requested resources are authorised: narrows
///   the principal's audience to <em>exactly</em> the requested set,
///   replacing the broader scope-bound list. The issued JWT's <c>aud</c>
///   claim then equals the value(s) the client asked for — the binding
///   that makes a token unable to replay against any other resource.</description></item>
/// </list>
///
/// <para>When the client doesn't send a <c>resource</c> parameter at all,
/// the handler is a no-op — current scope-driven audience behaviour is
/// preserved for clients that haven't been updated for RFC 8707 yet.</para>
///
/// <para>This is the prerequisite for the modgud IdP being usable as
/// the authorization server for MCP-spec-compliant Model Context Protocol
/// servers. Without resource binding, an access token issued for an MCP
/// server would be replayable against every other resource sharing the
/// IdP — a serious cross-resource confused-deputy hazard.</para>
/// </summary>
public sealed class ResourceIndicatorHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
            .UseSingletonHandler<ResourceIndicatorHandler>()
            // Run after the upstream code has populated the principal's
            // scopes + scope-derived resources, but before the access
            // token is generated so our narrowing takes effect on `aud`.
            .SetOrder(OpenIddictServerHandlers.GenerateAccessToken.Descriptor.Order - 5)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        // No principal yet → upstream rejected the request; nothing to do.
        if (context.Principal is null) return default;

        var requestedResources = context.Request.GetResources().ToList();
        if (requestedResources.Count == 0) return default;

        // Validate each requested resource is one the principal was
        // already granted via scope-binding (or client_id fallback).
        var grantedResources = context.Principal.GetResources().ToHashSet(StringComparer.Ordinal);

        foreach (var requested in requestedResources)
        {
            if (!grantedResources.Contains(requested))
            {
                context.Reject(
                    error: Errors.InvalidTarget,
                    description: $"The resource '{requested}' is not authorised for the requested scopes or client.");
                return default;
            }
        }

        // All requested resources are authorised — narrow the audience to
        // exactly what was requested. The issued JWT's `aud` will be this
        // set; the broader scope-bound resources (and the client_id
        // fallback) drop off.
        context.Principal.SetResources(requestedResources);
        return default;
    }
}
