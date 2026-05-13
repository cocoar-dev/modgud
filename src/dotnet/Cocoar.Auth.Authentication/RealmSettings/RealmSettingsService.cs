using System.Text.RegularExpressions;
using BuildingBlocks.Helper;
using Cocoar.Auth.Application.DTOs.RealmSettings;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Authentication.SelfRegistration.Captcha;
using Cocoar.Auth.Domain.Realms;
using ErrorOr;
using Marten;
using RealmSettingsDoc = Cocoar.Auth.Domain.RealmSettings.RealmSettings;

namespace Cocoar.Auth.Authentication.RealmSettings;

/// <summary>
/// Tenant-scoped service for the singleton <c>RealmSettings</c> document.
/// Read returns defaults when the doc is missing; Patch lazy-creates it
/// on first write so realms don't need explicit seeding. Captcha-secret
/// encryption is handled in-process here (CaptchaSecretStore is injected)
/// so the public API surface stays plumbing-free.
/// </summary>
public interface IRealmSettingsService
{
    Task<RealmSettingsDoc> LoadAsync(CancellationToken ct = default);
    Task<RealmSettingsDto> GetDtoAsync(CancellationToken ct = default);
    Task<ErrorOr<RealmSettingsDto>> PatchAsync(UpdateRealmSettingsDto dto, CancellationToken ct = default);
}

public sealed class RealmSettingsService(
    IDocumentSession session,
    CaptchaSecretStore captchaStore) : IRealmSettingsService
{
    public async Task<RealmSettingsDoc> LoadAsync(CancellationToken ct = default)
    {
        var doc = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
        // Caller decides whether a missing doc is "defaults" (read path)
        // or needs to be created (patch path). Returning null surfaces
        // the absence; the DTO mapper substitutes defaults.
        return doc ?? new RealmSettingsDoc();
    }

    public async Task<RealmSettingsDto> GetDtoAsync(CancellationToken ct = default)
    {
        var doc = await LoadAsync(ct);
        return ToDto(doc);
    }

    public async Task<ErrorOr<RealmSettingsDto>> PatchAsync(UpdateRealmSettingsDto dto, CancellationToken ct = default)
    {
        var existing = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
        var isCreate = existing is null;
        var doc = existing ?? new RealmSettingsDoc
        {
            Id = RealmSettingsDoc.SingletonId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (dto.SelfRegistration is not null)
        {
            doc.SelfRegistration = ApplySelfRegistrationPatch(doc.SelfRegistration, dto.SelfRegistration);
        }

        if (dto.Dcr is not null)
        {
            doc.Dcr = ApplyDcrPatch(doc.Dcr, dto.Dcr);
        }

        if (dto.Branding is not null)
        {
            var branding = ApplyBrandingPatch(doc.Branding, dto.Branding);
            if (branding.IsError) return branding.FirstError;
            doc.Branding = branding.Value;
        }

        if (!isCreate) doc.UpdatedAt = DateTimeOffset.UtcNow;

        session.Store(doc);
        await session.SaveChangesAsync(ct);

        return ToDto(doc);
    }

    private SelfRegistrationSettings ApplySelfRegistrationPatch(
        SelfRegistrationSettings? current,
        UpdateSelfRegistrationDto patch)
    {
        var s = current ?? new SelfRegistrationSettings();
        return s with
        {
            Enabled = patch.Enabled ?? s.Enabled,
            RequireEmailVerification = patch.RequireEmailVerification ?? s.RequireEmailVerification,
            AllowedEmailDomains = patch.AllowedEmailDomains ?? s.AllowedEmailDomains,
            RequireAdminApproval = patch.RequireAdminApproval ?? s.RequireAdminApproval,
            DefaultGroupIds = patch.DefaultGroupIds ?? s.DefaultGroupIds,
            TermsOfServiceUrl = patch.TermsOfServiceUrl ?? s.TermsOfServiceUrl,
            PrivacyPolicyUrl = patch.PrivacyPolicyUrl ?? s.PrivacyPolicyUrl,
            CaptchaEnabled = patch.CaptchaEnabled ?? s.CaptchaEnabled,
            CaptchaSiteKey = patch.CaptchaSiteKey ?? s.CaptchaSiteKey,
            EncryptedCaptchaSecret = patch.CaptchaSecret switch
            {
                null => s.EncryptedCaptchaSecret,                        // no change
                "" => null,                                              // clear (revert to Cocoar-default)
                var plain => captchaStore.Encrypt(plain),                // replace
            },
        };
    }

    internal static RealmSettingsDto ToDto(RealmSettingsDoc doc) => new()
    {
        SelfRegistration = MapSelfRegistrationToDto(doc.SelfRegistration),
        Dcr = MapDcrToDto(doc.Dcr),
        Branding = MapBrandingToDto(doc.Branding),
    };

    private static ErrorOr<BrandingSettings> ApplyBrandingPatch(
        BrandingSettings? current,
        UpdateBrandingSettingsDto patch)
    {
        var s = current ?? new BrandingSettings();
        // Tri-state per field: missing/null = no change, "" = clear (revert
        // to Cocoar default), other = replace. Matches the captcha-secret
        // semantics on the self-registration section.
        var color = MergeBrandingField(s.PrimaryColor, patch.PrimaryColor);
        if (color is not null && !IsValidCssColor(color))
            return Error.Validation("Branding.InvalidPrimaryColor",
                "PrimaryColor must be a hex (#rgb / #rrggbb / #rrggbbaa), rgb()/rgba(), hsl()/hsla(), or a CSS named-color.");

        var productName = MergeBrandingField(s.ProductName, patch.ProductName);
        if (productName is not null && productName.Length > 100)
            return Error.Validation("Branding.ProductNameTooLong",
                "ProductName must be 100 characters or fewer.");

        var logoAsset = MergeAssetIdField(s.LogoAssetId, patch.LogoAssetId);
        if (logoAsset.IsError) return logoAsset.FirstError;

        var faviconAsset = MergeAssetIdField(s.FaviconAssetId, patch.FaviconAssetId);
        if (faviconAsset.IsError) return faviconAsset.FirstError;

        return s with
        {
            ProductName = productName,
            LogoAssetId = logoAsset.Value,
            FaviconAssetId = faviconAsset.Value,
            PrimaryColor = color,
        };
    }

    private static string? MergeBrandingField(string? current, string? patch) => patch switch
    {
        null => current,
        "" => null,
        var v => v,
    };

    private static ErrorOr<Guid?> MergeAssetIdField(Guid? current, string? patch)
    {
        if (patch is null) return current;
        if (patch.Length == 0) return (Guid?)null;
        if (ShortGuid.TryDecode(patch, out var parsed)) return (Guid?)parsed;
        return Error.Validation("Branding.InvalidAssetId",
            "Asset id must be a ShortGuid or empty string to clear.");
    }

    // CSS color tokens we accept on write. Strict (no calc(), no var())
    // to keep injection-risk to zero: this value gets dropped into
    // --coar-color-primary which is then var()-consumed in property
    // values across the SPA.
    private static readonly Regex CssColorRegex = new(
        @"^(#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})" +
        @"|rgb\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*\)" +
        @"|rgba\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*(0|1|0?\.\d+)\s*\)" +
        @"|hsl\(\s*\d{1,3}\s*,\s*\d{1,3}%\s*,\s*\d{1,3}%\s*\)" +
        @"|hsla\(\s*\d{1,3}\s*,\s*\d{1,3}%\s*,\s*\d{1,3}%\s*,\s*(0|1|0?\.\d+)\s*\)" +
        @"|[a-zA-Z]{3,30})$",
        RegexOptions.Compiled);

    private static bool IsValidCssColor(string value) =>
        !string.IsNullOrWhiteSpace(value) && CssColorRegex.IsMatch(value.Trim());

    internal static BrandingSettingsDto MapBrandingToDto(BrandingSettings? s)
    {
        if (s is null) return new BrandingSettingsDto();
        return new BrandingSettingsDto
        {
            ProductName = s.ProductName,
            LogoAssetId = s.LogoAssetId is { } lid ? ShortGuid.Encode(lid) : null,
            LogoUrl = s.LogoAssetId is { } l ? $"/api/assets/{ShortGuid.Encode(l)}" : null,
            FaviconAssetId = s.FaviconAssetId is { } fid ? ShortGuid.Encode(fid) : null,
            FaviconUrl = s.FaviconAssetId is { } f ? $"/api/assets/{ShortGuid.Encode(f)}" : null,
            PrimaryColor = s.PrimaryColor,
        };
    }

    private static DcrSettings ApplyDcrPatch(DcrSettings? current, UpdateDcrSettingsDto patch)
    {
        var s = current ?? new DcrSettings();
        return s with
        {
            Enabled = patch.Enabled ?? s.Enabled,
            AccessTokenLifetime = patch.AccessTokenLifetimeMinutes is { } atm
                ? TimeSpan.FromMinutes(atm)
                : s.AccessTokenLifetime,
            RefreshTokenLifetime = patch.RefreshTokenLifetimeDays is { } rtd
                ? TimeSpan.FromDays(rtd)
                : s.RefreshTokenLifetime,
            GcTtlDays = patch.GcTtlDays ?? s.GcTtlDays,
            PerIpRateLimitPerHour = patch.PerIpRateLimitPerHour ?? s.PerIpRateLimitPerHour,
            PerRealmRateLimitPerDay = patch.PerRealmRateLimitPerDay ?? s.PerRealmRateLimitPerDay,
            ReservedNames = patch.ReservedNames ?? s.ReservedNames,
        };
    }

    internal static DcrSettingsDto MapDcrToDto(DcrSettings? s)
    {
        if (s is null) return new DcrSettingsDto();
        return new DcrSettingsDto
        {
            Enabled = s.Enabled,
            AccessTokenLifetimeMinutes = (int)s.AccessTokenLifetime.TotalMinutes,
            RefreshTokenLifetimeDays = (int)s.RefreshTokenLifetime.TotalDays,
            GcTtlDays = s.GcTtlDays,
            PerIpRateLimitPerHour = s.PerIpRateLimitPerHour,
            PerRealmRateLimitPerDay = s.PerRealmRateLimitPerDay,
            ReservedNames = s.ReservedNames,
        };
    }

    internal static SelfRegistrationDto MapSelfRegistrationToDto(SelfRegistrationSettings? s)
    {
        if (s is null) return new SelfRegistrationDto();
        return new SelfRegistrationDto
        {
            Enabled = s.Enabled,
            RequireEmailVerification = s.RequireEmailVerification,
            AllowedEmailDomains = s.AllowedEmailDomains,
            RequireAdminApproval = s.RequireAdminApproval,
            DefaultGroupIds = s.DefaultGroupIds,
            TermsOfServiceUrl = s.TermsOfServiceUrl,
            PrivacyPolicyUrl = s.PrivacyPolicyUrl,
            CaptchaEnabled = s.CaptchaEnabled,
            CaptchaSiteKey = s.CaptchaSiteKey,
            CaptchaSecretSet = s.EncryptedCaptchaSecret is { Length: > 0 },
        };
    }
}
