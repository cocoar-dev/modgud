using System.Text.Json;
using Cocoar.Auth.Domain.OAuth.Apis;
using Cocoar.Auth.Domain.OAuth.Applications;
using Marten;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Enforces the DCR-specific half of the resource-indicator contract:
/// clients minted via Dynamic Client Registration can only request
/// access tokens for resource servers that the realm-admin has opted in
/// via <c>OAuthApi.AllowDynamicRegistration</c>.
///
/// <para>The existing <see cref="ResourceIndicatorHandler"/> validates
/// that the requested <c>resource=</c> is one the principal would have
/// been granted via scope-binding. It does NOT know about the
/// <c>AllowDynamicRegistration</c> flag on <c>OAuthApi</c>. This sibling
/// handler runs after it and adds the second check — for DCR-issued
/// clients only — keeping the existing scope-containment logic
/// untouched and isolating the DCR cost (one extra tenant-DB read per
/// DCR-token issuance) from non-DCR flows.</para>
///
/// <para>Defense in depth: even if a DCR client's scope set looked safe
/// at registration time, the admin can later flip
/// <c>AllowDynamicRegistration</c> off on the RS to revoke DCR access
/// without rewriting every minted client. The flag re-check at every
/// token-issue is what makes that revocation immediate.</para>
///
/// <para>Per the v1 design: DCR clients without a <c>resource=</c>
/// parameter are rejected — there's no implicit audience for DCR
/// tokens, every request must opt into a specific RS.</para>
/// </summary>
public sealed class DcrAudienceContainmentHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
            .UseScopedHandler<DcrAudienceContainmentHandler>()
            // Order after ResourceIndicatorHandler so we observe the
            // already-validated + already-narrowed resource set. The
            // narrowing is irrelevant to our check (we work off
            // context.Request.GetResources()), but the ordering keeps
            // the failure modes deterministic — a missing scope grant
            // hits ResourceIndicatorHandler first, our DCR-only check
            // never runs for already-rejected requests.
            .SetOrder(ResourceIndicatorHandler.Descriptor.Order + 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IDocumentSession _session;

    public DcrAudienceContainmentHandler(IDocumentSession session)
    {
        _session = session;
    }

    public async ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        if (context.Principal is null) return;

        var clientId = context.Request.ClientId;
        if (string.IsNullOrEmpty(clientId)) return;

        var application = await _session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(x => x.ClientId == clientId && !x.IsDeleted);
        if (application is null) return;

        if (!IsDcrClient(application.Properties)) return;

        // DCR clients MUST request a resource. v1 design: no implicit
        // audience for DCR tokens — every request opts into a specific
        // RS so the audit log records which target the agent actually
        // hit.
        var requestedResources = context.Request.GetResources().ToList();
        if (requestedResources.Count == 0)
        {
            context.Reject(
                error: Errors.InvalidTarget,
                description: "DCR-registered clients must include a resource= parameter identifying the target resource server.");
            return;
        }

        // Look the requested resources up by OAuthApi.Name. RFC 8707 §2
        // mandates absolute-URI form, and OAuthApi.Name doubles as both
        // the audience claim and the resource= value, so the lookup is
        // a single Where over the indexed Name column.
        var apis = await _session.Query<OAuthApiState>()
            .Where(x => !x.IsDeleted && requestedResources.Contains(x.Name))
            .ToListAsync();
        var apisByName = apis.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var requested in requestedResources)
        {
            if (!apisByName.TryGetValue(requested, out var api))
            {
                context.Reject(
                    error: Errors.InvalidTarget,
                    description: $"The resource '{requested}' is not a registered resource server on this realm.");
                return;
            }

            if (!IsAllowedForDcr(api.Properties))
            {
                context.Reject(
                    error: Errors.InvalidTarget,
                    description: $"The resource '{requested}' is not approved for Dynamic Client Registration. Ask the realm admin to enable AllowDynamicRegistration on this resource server.");
                return;
            }
        }
    }

    private static bool IsDcrClient(IDictionary<string, object?> props)
        => GetBool(props, OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered);

    private static bool IsAllowedForDcr(IDictionary<string, object?> props)
        => GetBool(props, OAuthApiPropertyKeys.AllowDynamicRegistration);

    private static bool GetBool(IDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return false;
        return raw switch
        {
            bool b => b,
            JsonElement e when e.ValueKind is JsonValueKind.True => true,
            JsonElement e when e.ValueKind is JsonValueKind.False => false,
            _ => false,
        };
    }
}
