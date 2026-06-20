using System.Text.RegularExpressions;
using BuildingBlocks.Helper;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.DTOs.Realms;
using Modgud.Authentication.SelfRegistration.Captcha;
using Modgud.Domain.Realms;
using ErrorOr;
using Marten;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Authentication.RealmSettings;

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
            var dcr = ApplyDcrPatch(doc.Dcr, dto.Dcr);
            if (dcr.IsError) return dcr.FirstError;
            doc.Dcr = dcr.Value;
        }

        if (dto.Cimd is not null)
        {
            var cimd = ApplyCimdPatch(doc.Cimd, dto.Cimd);
            if (cimd.IsError) return cimd.FirstError;
            doc.Cimd = cimd.Value;
        }

        if (dto.NativeGrants is not null)
        {
            var native = ApplyNativeGrantsPatch(doc.NativeGrants, dto.NativeGrants);
            if (native.IsError) return native.FirstError;
            doc.NativeGrants = native.Value;
        }

        if (dto.Branding is not null)
        {
            var branding = ApplyBrandingPatch(doc.Branding, dto.Branding);
            if (branding.IsError) return branding.FirstError;
            doc.Branding = branding.Value;
        }

        if (dto.Deletion is not null)
        {
            var deletion = ApplyDeletionPatch(doc.Deletion, dto.Deletion);
            if (deletion.IsError) return deletion.FirstError;
            doc.Deletion = deletion.Value;
        }

        if (dto.Audit is not null)
        {
            var audit = ApplyAuditPatch(doc.Audit, dto.Audit);
            if (audit.IsError) return audit.FirstError;
            doc.Audit = audit.Value;
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
        Cimd = MapCimdToDto(doc.Cimd),
        NativeGrants = MapNativeGrantsToDto(doc.NativeGrants),
        Branding = MapBrandingToDto(doc.Branding),
        Deletion = MapDeletionToDto(doc.Deletion),
        Audit = MapAuditToDto(doc.Audit),
        Pages = doc.Pages is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(doc.Pages),
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

    private static ErrorOr<DcrSettings> ApplyDcrPatch(DcrSettings? current, UpdateDcrSettingsDto patch)
    {
        var s = current ?? new DcrSettings();
        var merged = s with
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

        if (ValidateTokenLifetimes("Dcr", merged.AccessTokenLifetime, merged.RefreshTokenLifetime) is { } error)
            return error;
        return merged;
    }

    // Shared bounds for the per-realm JWT-access-token-flow lifetimes (DCR, CIMD,
    // native grants). All three mint self-contained, individually-non-revocable
    // JWT access tokens for unverified / native clients, so the short access TTL
    // is the only bound on a leaked token — reject degenerate / over-long values
    // rather than let an admin configure an effectively-permanent token that no
    // logout or security-stamp rotation can recall.
    private static Error? ValidateTokenLifetimes(string section, TimeSpan access, TimeSpan refresh)
    {
        if (access.TotalMinutes < 1 || access.TotalMinutes > 60)
            return Error.Validation($"{section}.InvalidAccessTokenLifetime",
                "AccessTokenLifetimeMinutes must be between 1 and 60.");
        if (refresh.TotalDays < 1 || refresh.TotalDays > 30)
            return Error.Validation($"{section}.InvalidRefreshTokenLifetime",
                "RefreshTokenLifetimeDays must be between 1 and 30.");
        return null;
    }

    private static ErrorOr<DeletionSettings> ApplyDeletionPatch(DeletionSettings? current, UpdateDeletionSettingsDto patch)
    {
        var s = current ?? new DeletionSettings();
        var merged = s with
        {
            GraceDays = patch.GraceDays ?? s.GraceDays,
            ReminderLeadDays = patch.ReminderLeadDays ?? s.ReminderLeadDays,
            AdminRetentionDays = patch.AdminRetentionDays ?? s.AdminRetentionDays,
            AutoPurgeEnabled = patch.AutoPurgeEnabled ?? s.AutoPurgeEnabled,
        };

        if (merged.GraceDays < 1)
            return Error.Validation("Deletion.InvalidGraceDays", "GraceDays must be at least 1.");
        if (merged.ReminderLeadDays < 0)
            return Error.Validation("Deletion.InvalidReminderLeadDays", "ReminderLeadDays cannot be negative.");
        if (merged.ReminderLeadDays >= merged.GraceDays)
            return Error.Validation("Deletion.ReminderLeadTooLong",
                "ReminderLeadDays must be less than GraceDays, otherwise the reminder can never fire.");
        if (merged.AdminRetentionDays < 0)
            return Error.Validation("Deletion.InvalidAdminRetentionDays", "AdminRetentionDays cannot be negative.");

        return merged;
    }

    internal static DeletionSettingsDto MapDeletionToDto(DeletionSettings? s)
    {
        s ??= DeletionSettings.Defaults;
        return new DeletionSettingsDto
        {
            GraceDays = s.GraceDays,
            ReminderLeadDays = s.ReminderLeadDays,
            AdminRetentionDays = s.AdminRetentionDays,
            AutoPurgeEnabled = s.AutoPurgeEnabled,
        };
    }

    private static ErrorOr<AuditSettings> ApplyAuditPatch(AuditSettings? current, UpdateAuditSettingsDto patch)
    {
        var s = current ?? new AuditSettings();
        var merged = s with { VisibilityWindowDays = patch.VisibilityWindowDays ?? s.VisibilityWindowDays };
        if (merged.VisibilityWindowDays < 1)
            return Error.Validation("Audit.InvalidVisibilityWindowDays",
                "VisibilityWindowDays must be at least 1.");
        return merged;
    }

    internal static AuditSettingsDto MapAuditToDto(AuditSettings? s)
    {
        s ??= AuditSettings.Defaults;
        return new AuditSettingsDto { VisibilityWindowDays = s.VisibilityWindowDays };
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

    private static ErrorOr<CimdSettings> ApplyCimdPatch(CimdSettings? current, UpdateCimdSettingsDto patch)
    {
        var s = current ?? new CimdSettings();
        var merged = s with
        {
            Enabled = patch.Enabled ?? s.Enabled,
            AccessTokenLifetime = patch.AccessTokenLifetimeMinutes is { } atm
                ? TimeSpan.FromMinutes(atm)
                : s.AccessTokenLifetime,
            RefreshTokenLifetime = patch.RefreshTokenLifetimeDays is { } rtd
                ? TimeSpan.FromDays(rtd)
                : s.RefreshTokenLifetime,
        };

        if (ValidateTokenLifetimes("Cimd", merged.AccessTokenLifetime, merged.RefreshTokenLifetime) is { } error)
            return error;
        return merged;
    }

    internal static CimdSettingsDto MapCimdToDto(CimdSettings? s)
    {
        if (s is null) return new CimdSettingsDto();
        return new CimdSettingsDto
        {
            Enabled = s.Enabled,
            AccessTokenLifetimeMinutes = (int)s.AccessTokenLifetime.TotalMinutes,
            RefreshTokenLifetimeDays = (int)s.RefreshTokenLifetime.TotalDays,
        };
    }

    private static ErrorOr<NativeGrantSettings> ApplyNativeGrantsPatch(NativeGrantSettings? current, UpdateNativeGrantSettingsDto patch)
    {
        var s = current ?? new NativeGrantSettings();
        var merged = s with
        {
            Enabled = patch.Enabled ?? s.Enabled,
            AccessTokenLifetime = patch.AccessTokenLifetimeMinutes is { } atm
                ? TimeSpan.FromMinutes(atm)
                : s.AccessTokenLifetime,
            RefreshTokenLifetime = patch.RefreshTokenLifetimeDays is { } rtd
                ? TimeSpan.FromDays(rtd)
                : s.RefreshTokenLifetime,
        };

        // ADR-0010 — bounds-check the lifetimes (shared with DCR/CIMD): the short
        // access TTL is the only bound on a non-revocable JWT access token.
        if (ValidateTokenLifetimes("NativeGrants", merged.AccessTokenLifetime, merged.RefreshTokenLifetime) is { } error)
            return error;
        return merged;
    }

    internal static NativeGrantSettingsDto MapNativeGrantsToDto(NativeGrantSettings? s)
    {
        // Source the never-configured display defaults from the domain record so
        // the admin UI can't drift from the engine's actual defaults.
        s ??= new NativeGrantSettings();
        return new NativeGrantSettingsDto
        {
            Enabled = s.Enabled,
            AccessTokenLifetimeMinutes = (int)s.AccessTokenLifetime.TotalMinutes,
            RefreshTokenLifetimeDays = (int)s.RefreshTokenLifetime.TotalDays,
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
