using System.Text.RegularExpressions;
using BuildingBlocks.Helper;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.DTOs.Realms;
using Modgud.Authorization.Principals;
using Modgud.Authentication.SelfRegistration.Captcha;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.Common;
using Modgud.Domain.Realms;
using Modgud.Domain.Assets;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.PositionTerminals;
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
    Task<PositionSecurityConsequencesDto> PreviewPositionSecurityAsync(
        UpdatePositionSecuritySettingsDto dto, CancellationToken ct = default);
    Task<ErrorOr<RealmSettingsDto>> PatchAsync(UpdateRealmSettingsDto dto, CancellationToken ct = default);
}

public sealed class RealmSettingsService(
    IDocumentSession session,
    CaptchaSecretStore captchaStore,
    ISecurityAuditLog? securityAudit = null,
    IStaffingRevoker? staffingRevoker = null) : IRealmSettingsService
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
        var previousSecurityRetentionDays =
            doc.Audit?.SecurityRetentionDays ?? AuditSettings.Defaults.SecurityRetentionDays;
        PositionSecurityConsequencesDto? positionSecurityConsequences = null;

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

        if (dto.BrowserSessions is not null)
        {
            var browserSessions = ApplyBrowserSessionPatch(doc.BrowserSessions, dto.BrowserSessions);
            if (browserSessions.IsError) return browserSessions.FirstError;
            doc.BrowserSessions = browserSessions.Value;
        }

        if (dto.ClientSessions is not null)
        {
            var clientSessions = ApplyClientSessionPatch(doc.ClientSessions, dto.ClientSessions);
            if (clientSessions.IsError) return clientSessions.FirstError;
            doc.ClientSessions = clientSessions.Value;
        }

        if (dto.PositionSecurity is not null)
        {
            positionSecurityConsequences = await PreviewPositionSecurityAsync(dto.PositionSecurity, ct);
            if (positionSecurityConsequences.HasConsequences && !dto.ConfirmPositionSecurityConsequences)
                return Error.Validation("PositionSecurity.ConfirmationRequired",
                    $"The stricter floor affects {positionSecurityConsequences.Positions.Count} positions, " +
                    $"{positionSecurityConsequences.TerminalIds.Count} terminal slots and " +
                    $"{positionSecurityConsequences.StaffingSessionIds.Count} active staffing sessions. " +
                    "Preview and confirm the consequences before saving.");

            doc.PositionSecurity = new Modgud.Domain.RealmSettings.PositionSecuritySettings
            {
                RequiredProofCapabilities = dto.PositionSecurity.RequiredProofCapabilities
                    ?? doc.PositionSecurity?.RequiredProofCapabilities,
                RequiredBindingCapabilities = dto.PositionSecurity.RequiredBindingCapabilities
                    ?? doc.PositionSecurity?.RequiredBindingCapabilities,
            };
        }

        if (dto.AuthRateLimits is not null)
        {
            var arl = ApplyAuthRateLimitsPatch(doc.AuthRateLimits, dto.AuthRateLimits);
            if (arl.IsError) return arl.FirstError;
            doc.AuthRateLimits = arl.Value;
        }

        if (dto.Branding is not null)
        {
            var branding = await ApplyBrandingPatchAsync(doc.Branding, dto.Branding, ct);
            if (branding.IsError) return branding.FirstError;
            doc.Branding = branding.Value;
        }

        if (dto.EmailBranding is not null)
        {
            var emailBranding = ApplyEmailBrandingPatch(doc.EmailBranding, dto.EmailBranding);
            if (emailBranding.IsError) return emailBranding.FirstError;
            doc.EmailBranding = emailBranding.Value;
        }

        if (dto.RegistrationFields is not null)
        {
            var rf = ApplyRegistrationFieldsPatch(doc.RegistrationFields, dto.RegistrationFields);
            if (rf.IsError) return rf.FirstError;
            doc.RegistrationFields = rf.Value;
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
        if (doc.Audit?.SecurityRetentionDays is { } retentionDays
            && retentionDays != previousSecurityRetentionDays)
        {
            if (securityAudit is null)
            {
                throw new InvalidOperationException(
                    "Changing security retention requires an audit-capable RealmSettingsService.");
            }

            securityAudit.StoreRequired(session, new SecurityAuditRecord
            {
                EventType = AuditEvents.SecurityRetentionChanged,
                Severity = AuditSeverity.Warning,
                OutcomeCode = AuditOutcomes.Succeeded,
                OperationCode = "change-retention",
                RetentionDays = retentionDays,
            });
        }
        await session.SaveChangesAsync(ct);

        // Deliberately best-effort after the durable settings write. Refresh
        // revalidation is the fail-closed backstop if one individual cascade
        // fails; callers may safely retry because the revoker is idempotent.
        if (positionSecurityConsequences is { StaffingSessionIds.Count: > 0 } && staffingRevoker is not null)
        {
            foreach (var encodedId in positionSecurityConsequences.StaffingSessionIds)
            {
                if (ShortGuid.TryDecode(encodedId, out var sessionId))
                    await staffingRevoker.EndSessionAsync(sessionId, StaffingSessionEndReason.PolicyTightened, ct);
            }
        }

        return ToDto(doc);
    }

    public async Task<PositionSecurityConsequencesDto> PreviewPositionSecurityAsync(
        UpdatePositionSecuritySettingsDto dto, CancellationToken ct = default)
    {
        var current = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
        var requiredProof = dto.RequiredProofCapabilities
            ?? current?.PositionSecurity?.RequiredProofCapabilities
            ?? ProofCapability.None;
        var requiredBinding = dto.RequiredBindingCapabilities
            ?? current?.PositionSecurity?.RequiredBindingCapabilities
            ?? BindingCapability.None;

        var positions = (await session.Query<PositionPrincipal>()
                .Where(p => !p.IsDeleted && p.TerminalPolicy.Enabled)
                .ToListAsync(ct))
            .ToDictionary(p => p.Id);

        var affectedPositions = positions.Values
            .Select(position => new PositionSecurityAffectedPositionDto(
                ShortGuid.Encode(position.Id),
                position.AccountName,
                position.TerminalPolicy.AllowedActivationProofs
                    .Where(id => !PositionTerminalSecurity.ProofMeetsFloor(id, requiredProof))
                    .ToArray(),
                position.TerminalPolicy.AllowedDeviceBindings
                    .Where(id => !PositionTerminalSecurity.BindingMeetsFloor(id, requiredBinding))
                    .ToArray()))
            .Where(p => p.ViolatingActivationProofs.Count > 0 || p.ViolatingDeviceBindings.Count > 0)
            .ToArray();

        if (positions.Count == 0)
            return new PositionSecurityConsequencesDto { Positions = affectedPositions };

        var positionIds = positions.Keys.ToArray();
        var terminals = await session.Query<TerminalEnrollment>()
            .Where(t => positionIds.Contains(t.PositionPrincipalId))
            .ToListAsync(ct);
        var affectedTerminalIds = terminals
            .Where(t => !PositionTerminalSecurity.BindingMeetsFloor(t.Binding, requiredBinding))
            .Select(t => t.Id)
            .ToHashSet();

        var activeSessions = await session.Query<StaffingSession>()
            .Where(s => s.Status == StaffingSessionStatus.Active && positionIds.Contains(s.PositionPrincipalId))
            .ToListAsync(ct);
        var affectedSessions = activeSessions.Where(s =>
        {
            var methodId = string.IsNullOrWhiteSpace(s.Evidence?.MethodId)
                ? ActivationProofMethodIds.PersonalPasskey
                : s.Evidence.MethodId;
            var binding = string.IsNullOrWhiteSpace(s.Evidence?.Binding)
                ? DeviceBindingIds.Dpop
                : s.Evidence.Binding;
            return !PositionTerminalSecurity.ProofMeetsFloor(methodId, requiredProof)
                   || !PositionTerminalSecurity.BindingMeetsFloor(binding, requiredBinding)
                   || affectedTerminalIds.Contains(s.TerminalEnrollmentId);
        });

        return new PositionSecurityConsequencesDto
        {
            Positions = affectedPositions,
            TerminalIds = affectedTerminalIds.Select(ShortGuid.Encode).ToArray(),
            StaffingSessionIds = affectedSessions.Select(s => ShortGuid.Encode(s.Id)).ToArray(),
        };
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
            TermsOfServiceUrl = MergeClearableField(s.TermsOfServiceUrl, patch.TermsOfServiceUrl),
            PrivacyPolicyUrl = MergeClearableField(s.PrivacyPolicyUrl, patch.PrivacyPolicyUrl),
            CaptchaEnabled = patch.CaptchaEnabled ?? s.CaptchaEnabled,
            CaptchaSiteKey = MergeClearableField(s.CaptchaSiteKey, patch.CaptchaSiteKey),
            // v2 merge-patch: absent = keep the stored secret; null/blank =
            // clear (revert to the Cocoar-default secret); value = replace.
            EncryptedCaptchaSecret = !patch.CaptchaSecret.HasValue
                ? s.EncryptedCaptchaSecret
                : string.IsNullOrEmpty(patch.CaptchaSecret.Value)
                    ? null
                    : captchaStore.Encrypt(patch.CaptchaSecret.Value),
        };
    }

    internal static RealmSettingsDto ToDto(RealmSettingsDoc doc) => new()
    {
        SelfRegistration = MapSelfRegistrationToDto(doc.SelfRegistration),
        Dcr = MapDcrToDto(doc.Dcr),
        Cimd = MapCimdToDto(doc.Cimd),
        NativeGrants = MapNativeGrantsToDto(doc.NativeGrants),
        BrowserSessions = MapBrowserSessionsToDto(doc.BrowserSessions),
        ClientSessions = MapClientSessionsToDto(doc.ClientSessions),
        PositionSecurity = new PositionSecuritySettingsDto
        {
            RequiredProofCapabilities = doc.PositionSecurity?.RequiredProofCapabilities,
            RequiredBindingCapabilities = doc.PositionSecurity?.RequiredBindingCapabilities,
        },
        AuthRateLimits = MapAuthRateLimitsToDto(doc.AuthRateLimits),
        Branding = MapBrandingToDto(doc.Branding),
        EmailBranding = MapEmailBrandingToDto(doc.EmailBranding),
        RegistrationFields = MapRegistrationFieldsToDto(doc.RegistrationFields),
        Deletion = MapDeletionToDto(doc.Deletion),
        Audit = MapAuditToDto(doc.Audit),
    };

    private static ErrorOr<BrandingSettings> ApplyBrandingPatch(
        BrandingSettings? current,
        UpdateBrandingSettingsDto patch)
    {
        var s = current ?? new BrandingSettings();
        // v2 merge-patch per field: absent = no change, explicit null (or a
        // blank string) = clear back to the Cocoar default, other = replace.
        var color = MergeClearableField(s.PrimaryColor, patch.PrimaryColor);
        if (color is not null && !IsValidCssColor(color))
            return Error.Validation("Branding.InvalidPrimaryColor",
                "PrimaryColor must be a hex (#rgb / #rrggbb / #rrggbbaa), rgb()/rgba(), hsl()/hsla(), or a CSS named-color.");

        var productName = MergeClearableField(s.ProductName, patch.ProductName);
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

    private async Task<ErrorOr<BrandingSettings>> ApplyBrandingPatchAsync(
        BrandingSettings? current,
        UpdateBrandingSettingsDto patch,
        CancellationToken ct)
    {
        var merged = ApplyBrandingPatch(current, patch);
        if (merged.IsError) return merged.Errors;

        var ids = new[] { merged.Value.LogoAssetId, merged.Value.FaviconAssetId }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        foreach (var id in ids)
        {
            if (await session.LoadAsync<Asset>(id, ct) is null)
                return Error.Validation("Branding.AssetNotFound",
                    $"Branding asset '{ShortGuid.Encode(id)}' does not exist in this realm.");
        }

        return merged.Value;
    }

    /// <summary>v2 merge-patch for one clearable string: absent = keep the
    /// stored value, explicit null (or a blank string — same normalization the
    /// entity modals use) = clear, other = replace.</summary>
    private static string? MergeClearableField(string? current, Optional<string?> patch)
        => !patch.HasValue ? current
            : string.IsNullOrEmpty(patch.Value) ? null
            : patch.Value;

    private static ErrorOr<Modgud.Domain.RealmSettings.EmailBrandingSettings> ApplyEmailBrandingPatch(
        Modgud.Domain.RealmSettings.EmailBrandingSettings? current,
        UpdateEmailBrandingSettingsDto patch)
    {
        var result = (current ?? new Modgud.Domain.RealmSettings.EmailBrandingSettings()) with
        {
            ProductName = MergeClearableField(current?.ProductName, patch.ProductName),
            SubjectPrefix = MergeClearableField(current?.SubjectPrefix, patch.SubjectPrefix),
            Preheader = MergeClearableField(current?.Preheader, patch.Preheader),
            FooterText = MergeClearableField(current?.FooterText, patch.FooterText),
            FromName = MergeClearableField(current?.FromName, patch.FromName),
            FromAddress = MergeClearableField(current?.FromAddress, patch.FromAddress),
            ReplyTo = MergeClearableField(current?.ReplyTo, patch.ReplyTo),
        };
        if (result.ProductName?.Length > 100 || result.SubjectPrefix?.Length > 100
            || result.Preheader?.Length > 200 || result.FooterText?.Length > 500)
            return Error.Validation("EmailBranding.ValueTooLong",
                "Product/subject prefix max 100, preheader 200 and footer 500 characters.");
        if (result.FromName?.Length > 100 || result.FromName?.IndexOfAny(['\r', '\n']) >= 0)
            return Error.Validation("EmailBranding.FromNameInvalid",
                "Sender display name must be at most 100 characters and contain no line breaks.");
        if (result.ReplyTo is not null && !System.Net.Mail.MailAddress.TryCreate(result.ReplyTo, out _))
            return Error.Validation("EmailBranding.ReplyToInvalid", "Reply-to must be a valid email address.");
        if (result.FromAddress is not null && !EmailAddressRules.IsBareAddress(result.FromAddress))
            return Error.Validation("EmailBranding.FromAddressInvalid",
                "Sender address must be a plain email address (no display name).");
        return result;
    }

    private static EmailBrandingSettingsDto MapEmailBrandingToDto(
        Modgud.Domain.RealmSettings.EmailBrandingSettings? settings) => new()
    {
        ProductName = settings?.ProductName,
        SubjectPrefix = settings?.SubjectPrefix,
        Preheader = settings?.Preheader,
        FooterText = settings?.FooterText,
        FromName = settings?.FromName,
        FromAddress = settings?.FromAddress,
        ReplyTo = settings?.ReplyTo,
    };

    private static ErrorOr<Guid?> MergeAssetIdField(Guid? current, Optional<string?> patch)
    {
        if (!patch.HasValue) return current;
        if (string.IsNullOrEmpty(patch.Value)) return (Guid?)null;
        if (ShortGuid.TryDecode(patch.Value, out var parsed)) return (Guid?)parsed;
        return Error.Validation("Branding.InvalidAssetId",
            "Asset id must be a ShortGuid, or null to clear.");
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

    private static ErrorOr<RegistrationFieldsSettings> ApplyRegistrationFieldsPatch(
        RegistrationFieldsSettings? current,
        UpdateRegistrationFieldsSettingsDto patch)
    {
        var s = current ?? new RegistrationFieldsSettings();

        var username = ParseRequirement("Username", patch.Username, s.Username);
        if (username.IsError) return username.FirstError;
        var firstname = ParseRequirement("Firstname", patch.Firstname, s.Firstname);
        if (firstname.IsError) return firstname.FirstError;
        var lastname = ParseRequirement("Lastname", patch.Lastname, s.Lastname);
        if (lastname.IsError) return lastname.FirstError;

        return s with
        {
            Username = username.Value,
            Firstname = firstname.Value,
            Lastname = lastname.Value,
        };
    }

    // Null/missing patch value = keep current; non-null = parse + replace.
    private static ErrorOr<FieldRequirement> ParseRequirement(string field, string? raw, FieldRequirement current)
    {
        if (raw is null) return current;
        if (!Enum.TryParse<FieldRequirement>(raw, ignoreCase: true, out var v))
            return Error.Validation($"RegistrationFields.Invalid{field}",
                $"{field} must be Off, Optional or Required.");
        return v;
    }

    internal static RegistrationFieldsSettingsDto MapRegistrationFieldsToDto(RegistrationFieldsSettings? s)
    {
        s ??= RegistrationFieldsSettings.Defaults;
        return new RegistrationFieldsSettingsDto
        {
            Username = s.Username.ToString(),
            Firstname = s.Firstname.ToString(),
            Lastname = s.Lastname.ToString(),
        };
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
        var merged = s with
        {
            VisibilityWindowDays = patch.VisibilityWindowDays ?? s.VisibilityWindowDays,
            SecurityRetentionDays = patch.SecurityRetentionDays ?? s.SecurityRetentionDays,
        };
        if (merged.VisibilityWindowDays < 1)
            return Error.Validation("Audit.InvalidVisibilityWindowDays",
                "VisibilityWindowDays must be at least 1.");
        if (merged.SecurityRetentionDays is < 1 or > 365)
            return Error.Validation("Audit.InvalidSecurityRetentionDays",
                "SecurityRetentionDays must be between 1 and 365.");
        return merged;
    }

    internal static AuditSettingsDto MapAuditToDto(AuditSettings? s)
    {
        s ??= AuditSettings.Defaults;
        return new AuditSettingsDto
        {
            VisibilityWindowDays = s.VisibilityWindowDays,
            SecurityRetentionDays = s.SecurityRetentionDays,
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

    // ADR 0019 — merge-patch v2 per policy and dimension: absent = unchanged, explicit
    // null = back to the shipped default, value = override. The pre-ADR-0019 single
    // per-IP rules are still accepted (manifest compatibility) and stored as LEGACY
    // overrides, which keeps the realm in log-only mode until an admin picks a mode.
    // Shared with ApplicationSettingsService for the sparse per-App override.
    internal static ErrorOr<AuthRateLimitSettings> ApplyAuthRateLimitsPatch(
        AuthRateLimitSettings? current, UpdateAuthRateLimitsDto patch)
    {
        var s = current ?? new AuthRateLimitSettings();
        var policies = new Dictionary<string, PolicyLimits>(s.Policies ?? new(), StringComparer.OrdinalIgnoreCase);

        if (patch.Policies is not null)
        {
            foreach (var (rawName, update) in patch.Policies)
            {
                if (!AuthRateLimitDefaults.TryParse(rawName, out var policy))
                    return Error.Validation("AuthRateLimits.UnknownPolicy", $"Unknown rate-limit policy '{rawName}'.");
                var name = AuthRateLimitDefaults.PolicyName(policy);
                if (update is null)
                {
                    policies.Remove(name);
                    continue;
                }

                var limits = policies.TryGetValue(name, out var existing) ? existing : new PolicyLimits();
                foreach (var (dimension, field) in new (RateLimitDimension, Optional<RateLimitRuleDto?>)[]
                         {
                             (RateLimitDimension.Source, update.Source),
                             (RateLimitDimension.SourceRegistration, update.SourceRegistration),
                             (RateLimitDimension.Target, update.Target),
                             (RateLimitDimension.Client, update.Client),
                             (RateLimitDimension.App, update.App),
                             (RateLimitDimension.Device, update.Device),
                         })
                {
                    if (!field.HasValue) continue;
                    var dto = field.Value;
                    if (dto is null)
                    {
                        limits = limits.With(dimension, null);
                        continue;
                    }
                    if (AuthRateLimitDefaults.For(policy, dimension) is null)
                        return Error.Validation($"AuthRateLimits.{name}.{dimension}",
                            $"The {dimension} dimension does not apply to the '{name}' policy.");
                    if (ValidateRateLimitRule($"{name}.{dimension}", dto) is { } err) return err;
                    if (AuthRateLimitDefaults.IsSignalOnly(policy, dimension) && !dto.Enabled)
                        return Error.Validation($"AuthRateLimits.{name}.{dimension}",
                            $"The {dimension} dimension of the '{name}' policy is a signal and cannot be switched off; tune its threshold instead.");
                    limits = limits.With(dimension, ToRule(dto));
                }

                if (limits.IsEmpty) policies.Remove(name);
                else policies[name] = limits;
            }
        }

        var allowlist = s.SourceAllowlist;
        if (patch.SourceAllowlist.HasValue)
        {
            var entries = (patch.SourceAllowlist.Value ?? [])
                .Select(e => e?.Trim() ?? string.Empty)
                .Where(e => e.Length > 0)
                .ToArray();
            foreach (var entry in entries)
            {
                if (!System.Net.IPNetwork.TryParse(entry, out _) && !System.Net.IPAddress.TryParse(entry, out _))
                    return Error.Validation("AuthRateLimits.SourceAllowlist",
                        $"'{entry}' is not an IP address or a CIDR range.");
            }
            allowlist = entries.Length == 0 ? null : entries;
        }

        var mode = patch.Mode.HasValue ? patch.Mode.Value : s.Mode;

        foreach (var (name, rule) in new (string, RateLimitRuleDto?)[]
                 {
                     (nameof(patch.NativeOtp), patch.NativeOtp),
                     (nameof(patch.MagicLink), patch.MagicLink),
                     (nameof(patch.PasswordReset), patch.PasswordReset),
                     (nameof(patch.EmailOtp), patch.EmailOtp),
                     (nameof(patch.EmailVerification), patch.EmailVerification),
                     (nameof(patch.PasskeyBegin), patch.PasskeyBegin),
                     (nameof(patch.Bootstrap), patch.Bootstrap),
                 })
        {
            if (rule is not null && ValidateRateLimitRule(name, rule) is { } err) return err;
        }

        var result = s with
        {
            Policies = policies.Count == 0 ? null : policies,
            SourceAllowlist = allowlist,
            Mode = mode,
            NativeOtp = patch.NativeOtp is { } a ? ToRule(a) : s.NativeOtp,
            MagicLink = patch.MagicLink is { } b ? ToRule(b) : s.MagicLink,
            PasswordReset = patch.PasswordReset is { } c ? ToRule(c) : s.PasswordReset,
            EmailOtp = patch.EmailOtp is { } d ? ToRule(d) : s.EmailOtp,
            EmailVerification = patch.EmailVerification is { } e ? ToRule(e) : s.EmailVerification,
            PasskeyBegin = patch.PasskeyBegin is { } f ? ToRule(f) : s.PasskeyBegin,
            Bootstrap = patch.Bootstrap is { } g ? ToRule(g) : s.Bootstrap,
        };
        if (patch.ClearLegacy == true)
        {
            result = result with
            {
                NativeOtp = null, MagicLink = null, PasswordReset = null, EmailOtp = null,
                EmailVerification = null, PasskeyBegin = null, Bootstrap = null,
            };
        }
        return result;

        static RateLimitRule ToRule(RateLimitRuleDto d) => new()
        {
            PermitLimit = d.PermitLimit,
            WindowMinutes = d.WindowMinutes,
            Burst = d.Burst,
            Enabled = d.Enabled,
        };
    }

    private static Error? ValidateRateLimitRule(string policy, RateLimitRuleDto rule)
    {
        if (rule.PermitLimit is < 1 or > 100_000)
            return Error.Validation($"AuthRateLimits.{policy}.PermitLimit",
                "PermitLimit must be between 1 and 100000.");
        if (rule.WindowMinutes is < 1 or > 1440)
            return Error.Validation($"AuthRateLimits.{policy}.WindowMinutes",
                "WindowMinutes must be between 1 and 1440 (24 hours).");
        if (rule.Burst is { } burst && burst is < 1 or > 100_000)
            return Error.Validation($"AuthRateLimits.{policy}.Burst",
                "Burst must be between 1 and 100000 (or empty for a fixed window).");
        return null;
    }

    internal static RateLimitRuleDto ToRuleDto(RateLimitRule r) => new()
    {
        PermitLimit = r.PermitLimit,
        WindowMinutes = r.WindowMinutes,
        Burst = r.Burst,
        Enabled = r.Enabled,
    };

    /// <summary>Effective values for every policy + what is stored.</summary>
    internal static AuthRateLimitsDto MapAuthRateLimitsToDto(AuthRateLimitSettings? s)
    {
        var dto = new AuthRateLimitsDto
        {
            Mode = AuthRateLimitSettings.EffectiveMode(s),
            SourceAllowlist = AuthRateLimitSettings.EffectiveAllowlist(s).ToArray(),
            LegacyOverridesPresent = s?.HasLegacyOverrides == true,
            Overrides = MapAuthRateLimitOverridesToDto(s),
        };
        foreach (var policy in AuthRateLimitDefaults.All)
        {
            var name = AuthRateLimitDefaults.PolicyName(policy);
            dto.Policies[name] = ToPolicyDto(policy, AuthRateLimitSettings.EffectivePolicy(s, policy));
            dto.Defaults[name] = ToPolicyDto(policy, AuthRateLimitDefaults.ForPolicy(policy));
        }
        return dto;

        static PolicyLimitsDto ToPolicyDto(AuthRateLimitPolicy policy, PolicyLimits eff) => new()
        {
            Source = eff.Source is { } so ? ToRuleDto(so) with { SignalOnly = AuthRateLimitDefaults.IsSignalOnly(policy, RateLimitDimension.Source) } : null,
            SourceRegistration = eff.SourceRegistration is { } sr ? ToRuleDto(sr) : null,
            Target = eff.Target is { } ta ? ToRuleDto(ta) : null,
            Client = eff.Client is { } cl ? ToRuleDto(cl) : null,
            App = eff.App is { } ap ? ToRuleDto(ap) : null,
            Device = eff.Device is { } de ? ToRuleDto(de) : null,
        };
    }

    /// <summary>The sparse stored shape (realm overrides or an App override); null when
    /// nothing is stored. Round-trips through <see cref="ApplyAuthRateLimitsPatch"/>.</summary>
    internal static UpdateAuthRateLimitsDto? MapAuthRateLimitOverridesToDto(AuthRateLimitSettings? s)
    {
        if (s is null || s.IsEmpty) return null;

        Dictionary<string, UpdatePolicyLimitsDto?>? policies = null;
        if (s.Policies is { Count: > 0 })
        {
            policies = new Dictionary<string, UpdatePolicyLimitsDto?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, limits) in s.Policies)
            {
                policies[name] = new UpdatePolicyLimitsDto
                {
                    Source = limits.Source is { } so ? new Optional<RateLimitRuleDto?>(ToRuleDto(so)) : default,
                    SourceRegistration = limits.SourceRegistration is { } sr ? new Optional<RateLimitRuleDto?>(ToRuleDto(sr)) : default,
                    Target = limits.Target is { } ta ? new Optional<RateLimitRuleDto?>(ToRuleDto(ta)) : default,
                    Client = limits.Client is { } cl ? new Optional<RateLimitRuleDto?>(ToRuleDto(cl)) : default,
                    App = limits.App is { } ap ? new Optional<RateLimitRuleDto?>(ToRuleDto(ap)) : default,
                    Device = limits.Device is { } de ? new Optional<RateLimitRuleDto?>(ToRuleDto(de)) : default,
                };
            }
        }

        return new UpdateAuthRateLimitsDto
        {
            Policies = policies,
            SourceAllowlist = s.SourceAllowlist is null ? default : new Optional<string[]?>(s.SourceAllowlist),
            Mode = s.Mode is null ? default : new Optional<RateLimitEnforcementMode?>(s.Mode),
            NativeOtp = s.NativeOtp is { } a ? ToRuleDto(a) : null,
            MagicLink = s.MagicLink is { } b ? ToRuleDto(b) : null,
            PasswordReset = s.PasswordReset is { } c ? ToRuleDto(c) : null,
            EmailOtp = s.EmailOtp is { } d ? ToRuleDto(d) : null,
            EmailVerification = s.EmailVerification is { } e ? ToRuleDto(e) : null,
            PasskeyBegin = s.PasskeyBegin is { } f ? ToRuleDto(f) : null,
            Bootstrap = s.Bootstrap is { } g ? ToRuleDto(g) : null,
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

    private static ErrorOr<BrowserSessionPolicy> ApplyBrowserSessionPatch(
        BrowserSessionPolicy? current,
        UpdateBrowserSessionPolicyDto patch)
    {
        var policy = current ?? BrowserSessionPolicy.Defaults;
        var merged = policy with
        {
            IdleLifetime = patch.IdleLifetimeMinutes is { } idle
                ? TimeSpan.FromMinutes(idle)
                : policy.IdleLifetime,
            AbsoluteLifetime = patch.AbsoluteLifetimeMinutes is { } absolute
                ? TimeSpan.FromMinutes(absolute)
                : policy.AbsoluteLifetime,
            AllowRememberMe = patch.AllowRememberMe ?? policy.AllowRememberMe,
        };

        if (merged.IdleLifetime < TimeSpan.FromMinutes(5) ||
            merged.IdleLifetime > TimeSpan.FromDays(365))
            return Error.Validation("BrowserSessions.InvalidIdleLifetime",
                "IdleLifetimeMinutes must be between 5 minutes and 365 days.");
        if (merged.AbsoluteLifetime < merged.IdleLifetime ||
            merged.AbsoluteLifetime > TimeSpan.FromDays(3650))
            return Error.Validation("BrowserSessions.InvalidAbsoluteLifetime",
                "AbsoluteLifetimeMinutes must be at least the idle lifetime and no more than 3650 days.");

        return merged;
    }

    private static ErrorOr<ClientSessionPolicy> ApplyClientSessionPatch(
        ClientSessionPolicy? current,
        UpdateClientSessionPolicyDto patch)
    {
        var policy = current ?? ClientSessionPolicy.Defaults;
        var merged = policy with
        {
            IdleLifetime = patch.IdleLifetimeDays is { } idle
                ? TimeSpan.FromDays(idle)
                : policy.IdleLifetime,
            AbsoluteLifetime = patch.AbsoluteLifetimeDays is { } absolute
                ? TimeSpan.FromDays(absolute)
                : policy.AbsoluteLifetime,
        };

        if (merged.IdleLifetime < TimeSpan.FromDays(1) ||
            merged.IdleLifetime > TimeSpan.FromDays(3650))
            return Error.Validation("ClientSessions.InvalidIdleLifetime",
                "IdleLifetimeDays must be between 1 and 3650.");
        if (merged.AbsoluteLifetime < merged.IdleLifetime ||
            merged.AbsoluteLifetime > TimeSpan.FromDays(3650))
            return Error.Validation("ClientSessions.InvalidAbsoluteLifetime",
                "AbsoluteLifetimeDays must be at least the idle lifetime and no more than 3650.");

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

    internal static BrowserSessionPolicyDto MapBrowserSessionsToDto(BrowserSessionPolicy? policy)
    {
        policy ??= BrowserSessionPolicy.Defaults;
        return new BrowserSessionPolicyDto
        {
            IdleLifetimeMinutes = checked((int)policy.IdleLifetime.TotalMinutes),
            AbsoluteLifetimeMinutes = checked((int)policy.AbsoluteLifetime.TotalMinutes),
            AllowRememberMe = policy.AllowRememberMe,
        };
    }

    internal static ClientSessionPolicyDto MapClientSessionsToDto(ClientSessionPolicy? policy)
    {
        policy ??= ClientSessionPolicy.Defaults;
        return new ClientSessionPolicyDto
        {
            IdleLifetimeDays = checked((int)policy.IdleLifetime.TotalDays),
            AbsoluteLifetimeDays = checked((int)policy.AbsoluteLifetime.TotalDays),
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
