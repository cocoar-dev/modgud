using System.Text.Json;
using Marten;
using Modgud.Application.Dcr;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Scopes;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Authorize-time half of <see cref="DynamicClientScopePolicy"/>: a dynamic
/// client (DCR-minted or CIMD-synthesized) that requests a scope the realm has
/// not opted in for dynamic clients is refused with <c>invalid_scope</c> and a
/// description that names the scope and the flag to flip.
///
/// <para>Ordered just BEFORE OpenIddict's <c>ValidateScopePermissions</c> on
/// purpose. A dynamic client's permissions are derived from the same policy,
/// so a scope that fails here also has no <c>scp:</c> permission — and
/// OpenIddict's check would reject it first with the generic ID2051 ("not
/// allowed to use the specified scope"), which tells an admin nothing about
/// <c>AllowDynamicRegistrationClients</c>. Pre-compute, not override: the
/// reject stops the pipeline, so <c>Order - 1</c> is the right side.</para>
///
/// <para>Runs after OpenIddict's <c>ValidateScopes</c>, so an unregistered
/// scope name still gets OpenIddict's own error; this handler only judges
/// scopes the realm knows.</para>
/// </summary>
public sealed class DynamicClientScopeHandler : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateAuthorizationRequestContext>()
            .UseScopedHandler<DynamicClientScopeHandler>()
            .SetOrder(OpenIddictServerHandlers.Authentication.ValidateScopePermissions.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IDocumentSession _session;
    private readonly IOpenIddictApplicationManager _applications;

    public DynamicClientScopeHandler(IDocumentSession session, IOpenIddictApplicationManager applications)
    {
        _session = session;
        _applications = applications;
    }

    public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        var clientId = context.ClientId;
        if (string.IsNullOrEmpty(clientId)) return;

        var requested = context.Request.GetScopes();
        if (requested.Length == 0) return;

        // Through the manager, not a direct query: ValidateClientId just loaded this
        // client, so this is a cache hit for every registered client, and the store
        // already falls back to the CIMD resolver for a synthesized one.
        if (await _applications.FindByClientIdAsync(clientId, context.CancellationToken) is not OAuthApplicationState application
            || !IsDynamicClient(application.Properties))
            return;

        var names = requested.ToArray();
        var scopes = await _session.Query<OAuthScopeState>()
            .Where(s => names.Contains(s.Name) && !s.IsDeleted)
            .ToListAsync(context.CancellationToken);

        var refused = scopes.FirstOrDefault(s => !DynamicClientScopePolicy.IsRequestable(s));
        if (refused is null) return;

        context.Reject(
            error: Errors.InvalidScope,
            description: DescribeRefusal(refused));
    }

    /// <summary>The same wording the authorize endpoint's own check uses, so an
    /// agent sees one message whichever layer catches it.</summary>
    public static string DescribeRefusal(OAuthScopeState scope)
    {
        if (!scope.Enabled)
            return $"Scope '{scope.Name}' is disabled on this realm.";
        if (StandardScopes.IsStandard(scope.Name))
            return $"Scope '{scope.Name}' is not available to dynamically registered clients.";
        return $"Scope '{scope.Name}' is not opted in for Dynamic Client Registration clients. " +
               "Ask the realm admin to enable AllowDynamicRegistrationClients on the scope.";
    }

    private static bool IsDynamicClient(IDictionary<string, object?> props)
    {
        if (!props.TryGetValue(OAuthApplicationPropertyKeys.DcrIsDynamicallyRegistered, out var raw) || raw is null)
            return false;
        return raw switch
        {
            bool b => b,
            JsonElement e when e.ValueKind is JsonValueKind.True => true,
            _ => false,
        };
    }
}
