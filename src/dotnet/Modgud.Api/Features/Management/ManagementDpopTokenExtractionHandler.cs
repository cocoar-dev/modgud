using System.Net.Http.Headers;
using Microsoft.AspNetCore;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Validation.OpenIddictValidationEvents;

namespace Modgud.Api.Features.Management;

/// <summary>Recognizes the DPoP scheme only on explicitly opted-in Management
/// endpoints. This extracts a token, not a proof; the endpoint filter must still
/// enforce its binding. No header rewriting or changes to other consumers.</summary>
public sealed class ManagementDpopTokenExtractionHandler : IOpenIddictValidationHandler<ProcessAuthenticationContext>
{
    public static OpenIddictValidationHandlerDescriptor Descriptor { get; } =
        OpenIddictValidationHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
            .UseSingletonHandler<ManagementDpopTokenExtractionHandler>()
            .SetOrder(OpenIddictValidationAspNetCoreHandlers.ExtractAccessTokenFromAuthorizationHeader.Descriptor.Order + 1)
            .SetType(OpenIddictValidationHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(ProcessAuthenticationContext context)
    {
        var request = context.Transaction.GetHttpRequest();
        if (request?.HttpContext.GetEndpoint()?.Metadata.GetMetadata<ManagementEndpointMetadata>() is not null
            && request.Headers.Authorization.Count == 1
            && AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var value)
            && value.Scheme.Equals("DPoP", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(value.Parameter))
        {
            context.AccessToken = value.Parameter;
        }
        return default;
    }
}

internal sealed class ManagementEndpointMetadata
{
    internal static ManagementEndpointMetadata Instance { get; } = new();
    private ManagementEndpointMetadata() { }
}
