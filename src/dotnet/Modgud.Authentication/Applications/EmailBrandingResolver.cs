using Microsoft.AspNetCore.Http;

namespace Modgud.Authentication.Applications;

/// <summary>
/// ADR-0011 Phase 6 — resolves the product name used in outbound emails. Email
/// branding is only Host-resolvable (no send-site carries a client_id), so this
/// reads the ambient request: on an Application subdomain it returns the App's
/// email/branding product name; on a plain tenant host the realm branding; and
/// "Modgud" as the final default (matching <c>BrandingSettings.ProductName</c>'s
/// documented fallback). With no request context (CLI/background) it returns the
/// default.
/// </summary>
public interface IEmailBrandingResolver
{
    Task<string> ResolveProductNameAsync(CancellationToken ct = default);
}

public sealed class EmailBrandingResolver(
    IHttpContextAccessor httpContextAccessor,
    IApplicationSettingsResolver settingsResolver) : IEmailBrandingResolver
{
    private const string Default = "Modgud";

    public async Task<string> ResolveProductNameAsync(CancellationToken ct = default)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
            return Default;

        var effective = await settingsResolver.ResolveForRequestAsync(httpContext, clientId: null, ct);
        return effective.EmailBranding?.ProductName
               ?? effective.Branding?.ProductName
               ?? Default;
    }
}
