using Microsoft.AspNetCore.Http;
using BuildingBlocks.Helper;

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
    Task<EmailBrandingContext> ResolveAsync(
        Guid? applicationId = null,
        string? clientId = null,
        CancellationToken ct = default);

    Task<Dictionary<string, string>> ApplyAsync(
        Dictionary<string, string> model,
        Guid? applicationId = null,
        string? clientId = null,
        CancellationToken ct = default);

    Task<string> ResolveProductNameAsync(CancellationToken ct = default);
}

public sealed record EmailBrandingContext(
    string ProductName,
    string? LogoUrl,
    string PrimaryColor,
    string Language,
    string? SubjectPrefix,
    string? Preheader,
    string? FooterText,
    string? FromName,
    string? FromAddress,
    string? ReplyTo);

public sealed class EmailBrandingResolver(
    IHttpContextAccessor httpContextAccessor,
    IApplicationSettingsResolver settingsResolver) : IEmailBrandingResolver
{
    private const string Default = "Modgud";

    public async Task<EmailBrandingContext> ResolveAsync(
        Guid? applicationId = null,
        string? clientId = null,
        CancellationToken ct = default)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var effective = applicationId is { } explicitApp
            ? await settingsResolver.ResolveAsync(explicitApp, ct)
            : httpContext is not null
                ? await settingsResolver.ResolveForRequestAsync(httpContext, clientId, ct)
                : await settingsResolver.ResolveAsync(null, ct);

        var productName = effective.EmailBranding?.ProductName
                          ?? effective.Branding?.ProductName
                          ?? Default;
        string? logoUrl = null;
        if (effective.Branding?.LogoAssetId is { } logoId && httpContext is not null)
        {
            logoUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}/api/assets/{ShortGuid.Encode(logoId)}";
        }

        var language = httpContext?.Request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(v => v.Quality ?? 1)
            .Select(v => v.Value.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?
            .StartsWith("en", StringComparison.OrdinalIgnoreCase) == true
            ? "en"
            : "de";

        return new EmailBrandingContext(
            productName,
            logoUrl,
            effective.Branding?.PrimaryColor ?? "#525e76",
            language,
            effective.EmailBranding?.SubjectPrefix,
            effective.EmailBranding?.Preheader,
            effective.EmailBranding?.FooterText,
            effective.EmailBranding?.FromName,
            effective.EmailBranding?.FromAddress,
            effective.EmailBranding?.ReplyTo);
    }

    public async Task<Dictionary<string, string>> ApplyAsync(
        Dictionary<string, string> model,
        Guid? applicationId = null,
        string? clientId = null,
        CancellationToken ct = default)
    {
        var branding = await ResolveAsync(applicationId, clientId, ct);
        model["AppName"] = branding.ProductName;
        model["PrimaryColor"] = branding.PrimaryColor;
        model["Language"] = branding.Language;
        if (branding.LogoUrl is not null) model["LogoUrl"] = branding.LogoUrl;
        if (branding.SubjectPrefix is not null) model["SubjectPrefix"] = branding.SubjectPrefix;
        if (branding.Preheader is not null) model["Preheader"] = branding.Preheader;
        if (branding.FooterText is not null) model["FooterText"] = branding.FooterText;
        if (branding.FromName is not null) model["FromName"] = branding.FromName;
        if (branding.FromAddress is not null) model["FromAddress"] = branding.FromAddress;
        if (branding.ReplyTo is not null) model["ReplyTo"] = branding.ReplyTo;
        return model;
    }

    public async Task<string> ResolveProductNameAsync(CancellationToken ct = default)
    {
        return (await ResolveAsync(ct: ct)).ProductName;
    }
}
