using System.Collections.Immutable;
using System.Text.Json;
using Modgud.Domain.OAuth.Applications;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// The <c>cocoar:enabled</c> switch of an OAuth client, read the same way everywhere.
/// Missing means enabled (clients from before the property existed); a value that is not a
/// JSON boolean counts as disabled (EG-04: fail closed, the writers only store booleans).
/// </summary>
public static class OAuthClientEnabled
{
    public static bool IsEnabled(ImmutableDictionary<string, JsonElement> properties) =>
        !properties.TryGetValue(OAuthApplicationPropertyKeys.Enabled, out var value)
        || value.ValueKind == JsonValueKind.True;
}

/// <summary>
/// EG-04 — <c>Enabled</c> was checked at the authorization and token endpoints only, so a
/// disabled resource-server client kept introspecting the tokens issued for it (active
/// status and claims). A disabled client is refused at the introspection endpoint with
/// <c>invalid_client</c>; unknown clients are left to OpenIddict's own validation.
/// </summary>
public sealed class DisabledClientIntrospectionHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.ValidateIntrospectionRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ValidateIntrospectionRequestContext>()
            .UseScopedHandler<DisabledClientIntrospectionHandler>()
            .SetOrder(int.MaxValue - 100_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly IOpenIddictApplicationManager _applications;

    public DisabledClientIntrospectionHandler(IOpenIddictApplicationManager applications)
        => _applications = applications;

    public async ValueTask HandleAsync(OpenIddictServerEvents.ValidateIntrospectionRequestContext context)
    {
        var clientId = context.Request.ClientId;
        if (string.IsNullOrEmpty(clientId)) return;

        var application = await _applications.FindByClientIdAsync(clientId, context.CancellationToken);
        if (application is null) return;

        var properties = await _applications.GetPropertiesAsync(application, context.CancellationToken);
        if (!OAuthClientEnabled.IsEnabled(properties))
        {
            context.Reject(
                error: Errors.InvalidClient,
                description: "The client application is disabled.");
        }
    }
}
