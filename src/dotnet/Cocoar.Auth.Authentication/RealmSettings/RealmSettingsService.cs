using Cocoar.Auth.Application.DTOs.RealmSettings;
using Cocoar.Auth.Application.DTOs.Realms;
using Cocoar.Auth.Authentication.SelfRegistration.Captcha;
using Cocoar.Auth.Domain.Realms;
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
    Task<RealmSettingsDto> PatchAsync(UpdateRealmSettingsDto dto, CancellationToken ct = default);
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

    public async Task<RealmSettingsDto> PatchAsync(UpdateRealmSettingsDto dto, CancellationToken ct = default)
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
    };

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
