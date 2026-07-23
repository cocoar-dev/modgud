using System.Text.RegularExpressions;
using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Modgud.Application.DTOs.Applications;
using Modgud.Authorization.Apps;
using Modgud.Domain.Applications;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Applications;

/// <summary>
/// ADR-0011 — admin write surface for a per-Application settings override (the
/// tenant-scoped <see cref="ApplicationSettings"/> doc keyed by <c>App.Id</c>).
/// Mirrors <c>RealmSettingsService</c> but sparse: a provided section REPLACES that
/// App's override; a null section is left unchanged. Setting <c>Origin.Subdomain</c>
/// additionally writes the global host→App routing map (and clearing it removes the
/// route) so the App's subdomain actually resolves at middleware time.
/// </summary>
public interface IApplicationSettingsService
{
    Task<ErrorOr<ApplicationSettingsDto>> GetAsync(Guid applicationId, CancellationToken ct = default);
    Task<ErrorOr<ApplicationSettingsDto>> PatchAsync(Guid applicationId, ApplicationSettingsDto dto, CancellationToken ct = default);

    /// <summary>
    /// Stages the per-App settings override (every section EXCEPT <c>Origin</c>) onto the
    /// caller's shared <see cref="IDocumentSession"/> WITHOUT committing, so it lands in the
    /// same tenant transaction as the App aggregate — the unified, atomic App create/update.
    /// <c>Origin</c> is excluded because it also drives the GLOBAL host→App routing map (a
    /// different database, so inherently a separate write): validate it up-front via
    /// <see cref="ValidateOriginAsync"/> and apply it via <see cref="PatchAsync"/> AFTER the
    /// atomic commit. No app-existence check — the caller (<c>AppAdminService</c>) guarantees it.
    /// </summary>
    Task<ErrorOr<Success>> StageNonOriginAsync(Guid applicationId, ApplicationSettingsDto dto, CancellationToken ct = default);

    /// <summary>
    /// Read-only validation of an Origin subdomain (format, child-of-the-realm-primary,
    /// cross-realm uniqueness) so a unified create/update can reject an invalid subdomain
    /// up-front — before committing anything. An empty/null subdomain (a clear) is always valid.
    /// </summary>
    Task<ErrorOr<Success>> ValidateOriginAsync(Guid applicationId, string? subdomain, CancellationToken ct = default);
}

public sealed class ApplicationSettingsService(
    IDocumentSession session,
    IGlobalStore globalStore,
    IRealmCache realmCache) : IApplicationSettingsService
{
    private static readonly Regex CssColorRegex = new(
        @"^(#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})" +
        @"|rgb\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*\)" +
        @"|rgba\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*(0|1|0?\.\d+)\s*\)" +
        @"|hsl\(\s*\d{1,3}\s*,\s*\d{1,3}%\s*,\s*\d{1,3}%\s*\)" +
        @"|hsla\(\s*\d{1,3}\s*,\s*\d{1,3}%\s*,\s*\d{1,3}%\s*,\s*(0|1|0?\.\d+)\s*\)" +
        @"|[a-zA-Z]{3,30})$",
        RegexOptions.Compiled);

    // Conservative hostname check (labels of a-z 0-9 hyphen, dot-separated).
    private static readonly Regex HostRegex = new(
        @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$",
        RegexOptions.Compiled);

    public async Task<ErrorOr<ApplicationSettingsDto>> GetAsync(Guid applicationId, CancellationToken ct = default)
    {
        if (await LoadAppAsync(applicationId, ct) is null)
            return Error.NotFound("Application.NotFound", "App not found in this realm.");

        var doc = await session.LoadAsync<ApplicationSettings>(applicationId, ct);
        return ToDto(doc);
    }

    public async Task<ErrorOr<ApplicationSettingsDto>> PatchAsync(
        Guid applicationId, ApplicationSettingsDto dto, CancellationToken ct = default)
    {
        if (await LoadAppAsync(applicationId, ct) is null)
            return Error.NotFound("Application.NotFound", "App not found in this realm.");

        var doc = await session.LoadAsync<ApplicationSettings>(applicationId, ct)
                  ?? new ApplicationSettings { Id = applicationId, CreatedAt = DateTimeOffset.UtcNow };
        var isCreate = doc.CreatedAt == default;
        if (isCreate) doc.CreatedAt = DateTimeOffset.UtcNow;

        if (dto.SelfRegistration is not null)
        {
            var r = MapSelfRegistration(dto.SelfRegistration);
            if (r.IsError) return r.FirstError;
            doc.SelfRegistration = r.Value;
        }

        if (dto.NativeGrants is not null)
        {
            var r = MapNativeGrants(dto.NativeGrants);
            if (r.IsError) return r.FirstError;
            doc.NativeGrants = r.Value;
        }

        if (dto.ClientSessions is not null)
        {
            var r = MapClientSessions(dto.ClientSessions);
            if (r.IsError) return r.FirstError;
            doc.ClientSessions = r.Value;
        }

        if (dto.Dcr is not null)
        {
            var r = MapDcr(dto.Dcr);
            if (r.IsError) return r.FirstError;
            doc.Dcr = r.Value;
        }

        if (dto.Cimd is not null)
        {
            var r = MapCimd(dto.Cimd);
            if (r.IsError) return r.FirstError;
            doc.Cimd = r.Value;
        }

        if (dto.Branding is not null)
        {
            var r = MapBranding(dto.Branding);
            if (r.IsError) return r.FirstError;
            doc.Branding = r.Value;
        }

        if (dto.EmailBranding is not null)
        {
            doc.EmailBranding = string.IsNullOrWhiteSpace(dto.EmailBranding.ProductName)
                ? null
                : new ApplicationEmailBranding { ProductName = dto.EmailBranding.ProductName!.Trim() };
        }

        if (dto.RegistrationFields is not null)
        {
            var r = MapRegistrationFields(dto.RegistrationFields);
            if (r.IsError) return r.FirstError;
            doc.RegistrationFields = r.Value;
        }

        // Origin / subdomain is special: it also drives the GLOBAL host→App routing
        // map, validated for child-of-primary-domain + cross-realm uniqueness.
        if (dto.Origin is not null)
        {
            var originResult = await ApplyOriginAsync(applicationId, dto.Origin.Subdomain, ct);
            if (originResult.IsError) return originResult.FirstError;
            doc.Origin = originResult.Value;
        }

        doc.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(doc);
        await session.SaveChangesAsync(ct);

        return ToDto(doc);
    }

    private async Task<App?> LoadAppAsync(Guid applicationId, CancellationToken ct)
    {
        var app = await session.LoadAsync<App>(applicationId, ct);
        return app is { IsDeleted: false } ? app : null;
    }

    // ── Atomic-create staging (every section except Origin, no commit) ────────
    // REPLACE semantics: the DTO is the COMPLETE desired override state (the unified App
    // create/update is a replace, not a sparse patch). A provided section sets the override;
    // a NULL section CLEARS it (→ inherit the realm). This is what keeps the modal honest —
    // an unchecked section round-trips back unchecked instead of as an empty-but-present
    // override. (PatchAsync stays sparse for the Origin-only follow-on.)

    public async Task<ErrorOr<Success>> StageNonOriginAsync(
        Guid applicationId, ApplicationSettingsDto dto, CancellationToken ct = default)
    {
        var doc = await session.LoadAsync<ApplicationSettings>(applicationId, ct)
                  ?? new ApplicationSettings { Id = applicationId, CreatedAt = DateTimeOffset.UtcNow };
        if (doc.CreatedAt == default) doc.CreatedAt = DateTimeOffset.UtcNow;

        if (dto.SelfRegistration is null) doc.SelfRegistration = null;
        else { var r = MapSelfRegistration(dto.SelfRegistration); if (r.IsError) return r.FirstError; doc.SelfRegistration = r.Value; }

        if (dto.NativeGrants is null) doc.NativeGrants = null;
        else { var r = MapNativeGrants(dto.NativeGrants); if (r.IsError) return r.FirstError; doc.NativeGrants = r.Value; }

        if (dto.ClientSessions is null) doc.ClientSessions = null;
        else { var r = MapClientSessions(dto.ClientSessions); if (r.IsError) return r.FirstError; doc.ClientSessions = r.Value; }

        if (dto.Dcr is null) doc.Dcr = null;
        else { var r = MapDcr(dto.Dcr); if (r.IsError) return r.FirstError; doc.Dcr = r.Value; }

        if (dto.Cimd is null) doc.Cimd = null;
        else { var r = MapCimd(dto.Cimd); if (r.IsError) return r.FirstError; doc.Cimd = r.Value; }

        if (dto.Branding is null) doc.Branding = null;
        else { var r = MapBranding(dto.Branding); if (r.IsError) return r.FirstError; doc.Branding = r.Value; }

        doc.EmailBranding = string.IsNullOrWhiteSpace(dto.EmailBranding?.ProductName)
            ? null
            : new ApplicationEmailBranding { ProductName = dto.EmailBranding.ProductName!.Trim() };

        if (dto.RegistrationFields is null) doc.RegistrationFields = null;
        else { var r = MapRegistrationFields(dto.RegistrationFields); if (r.IsError) return r.FirstError; doc.RegistrationFields = r.Value; }

        doc.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(doc);   // enrolled on the shared session; the caller commits.
        return ErrorOr.Result.Success;
    }

    // ── Origin / global routing map ──────────────────────────────────────────

    public async Task<ErrorOr<Success>> ValidateOriginAsync(
        Guid applicationId, string? subdomainRaw, CancellationToken ct = default)
    {
        var subdomain = subdomainRaw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(subdomain)) return ErrorOr.Result.Success;   // clearing is always valid

        var slug = TenantContext.Current;
        await using var gsession = globalStore.LightweightSession();
        var realm = await gsession.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == slug, ct);
        if (realm is null)
            return Error.Failure("Application.RealmNotFound", "The current realm could not be resolved.");

        if (!HostRegex.IsMatch(subdomain))
            return Error.Validation("Application.InvalidSubdomain", "Subdomain must be a valid hostname.");

        // Must be a child of the realm's primary domain (the cookie + routing
        // model: apps live under the tenant's primary domain).
        var primary = realm.PrimaryDomain.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(primary) || !subdomain.EndsWith("." + primary, StringComparison.Ordinal))
            return Error.Validation("Application.SubdomainNotUnderPrimary",
                $"Subdomain must be a child of the realm's primary domain ('{realm.PrimaryDomain}').");

        // Cross-realm uniqueness: the host must not be claimed by any realm's
        // plain domains or another App's route.
        var allRealms = await gsession.Query<Realm>().ToListAsync(ct);
        foreach (var r in allRealms)
        {
            if (r.Domains.Any(d => string.Equals(d, subdomain, StringComparison.OrdinalIgnoreCase)))
                return Error.Conflict("Application.SubdomainTaken", "That host is already a realm domain.");
            foreach (var kv in r.ApplicationDomains)
            {
                if (string.Equals(kv.Key, subdomain, StringComparison.OrdinalIgnoreCase)
                    && !(r.Id == realm.Id && kv.Value == applicationId))
                    return Error.Conflict("Application.SubdomainTaken", "That host is already mapped to an application.");
            }
        }

        return ErrorOr.Result.Success;
    }

    private async Task<ErrorOr<ApplicationOrigin?>> ApplyOriginAsync(
        Guid applicationId, string? subdomainRaw, CancellationToken ct)
    {
        var subdomain = subdomainRaw?.Trim().ToLowerInvariant();

        var valid = await ValidateOriginAsync(applicationId, subdomain, ct);
        if (valid.IsError) return valid.FirstError;

        var slug = TenantContext.Current;
        await using var gsession = globalStore.LightweightSession();
        var realm = await gsession.Query<Realm>().FirstOrDefaultAsync(r => r.Slug == slug, ct);
        if (realm is null)
            return Error.Failure("Application.RealmNotFound", "The current realm could not be resolved.");

        // Drop any existing host entries that currently point at this App (a
        // subdomain change or clear must not leave a stale route behind).
        var stale = realm.ApplicationDomains
            .Where(kv => kv.Value == applicationId)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var host in stale) realm.ApplicationDomains.Remove(host);

        ApplicationOrigin? origin = null;
        if (!string.IsNullOrEmpty(subdomain))
        {
            realm.ApplicationDomains[subdomain] = applicationId;
            origin = new ApplicationOrigin { Subdomain = subdomain };
        }

        gsession.Store(realm);
        await gsession.SaveChangesAsync(ct);
        realmCache.Invalidate();
        return origin;
    }

    // ── Mapping + validation (DTO → domain) ─────────────────────────────────

    private static ErrorOr<ApplicationSelfRegistration> MapSelfRegistration(ApplicationSelfRegistrationDto d)
    {
        SelfRegPosture? posture = null;
        if (!string.IsNullOrWhiteSpace(d.Posture))
        {
            if (!Enum.TryParse<SelfRegPosture>(d.Posture, ignoreCase: true, out var p))
                return Error.Validation("Application.InvalidPosture",
                    "Posture must be Off, JitOnOtp, ExplicitEndpoint or InviteCode.");
            posture = p;
        }

        return new ApplicationSelfRegistration
        {
            Posture = posture,
            Enabled = d.Enabled,
            RequireEmailVerification = d.RequireEmailVerification,
            AllowedEmailDomains = d.AllowedEmailDomains,
            RequireAdminApproval = d.RequireAdminApproval,
            DefaultGroupIds = d.DefaultGroupIds,
            TermsOfServiceUrl = NullIfBlank(d.TermsOfServiceUrl),
            PrivacyPolicyUrl = NullIfBlank(d.PrivacyPolicyUrl),
        };
    }

    private static ErrorOr<ApplicationNativeGrantOverrides> MapNativeGrants(ApplicationNativeGrantsDto d)
    {
        if (LifetimeError("NativeGrants", d.AccessTokenLifetimeMinutes, d.RefreshTokenLifetimeDays) is { } e) return e;
        return new ApplicationNativeGrantOverrides
        {
            Enabled = d.Enabled,
            AccessTokenLifetime = Minutes(d.AccessTokenLifetimeMinutes),
            RefreshTokenLifetime = Days(d.RefreshTokenLifetimeDays),
        };
    }

    private static ErrorOr<ApplicationClientSessionOverrides> MapClientSessions(ApplicationClientSessionsDto dto)
    {
        if (dto.IdleLifetimeDays is { } idle && (idle < 1 || idle > 3650))
            return Error.Validation("ClientSessions.InvalidIdleLifetime",
                "IdleLifetimeDays must be between 1 and 3650.");
        if (dto.AbsoluteLifetimeDays is { } absolute && (absolute < 1 || absolute > 3650))
            return Error.Validation("ClientSessions.InvalidAbsoluteLifetime",
                "AbsoluteLifetimeDays must be between 1 and 3650.");
        if (dto.IdleLifetimeDays is { } i && dto.AbsoluteLifetimeDays is { } a && a < i)
            return Error.Validation("ClientSessions.InvalidAbsoluteLifetime",
                "AbsoluteLifetimeDays must be at least IdleLifetimeDays.");

        return new ApplicationClientSessionOverrides
        {
            IdleLifetime = Days(dto.IdleLifetimeDays),
            AbsoluteLifetime = Days(dto.AbsoluteLifetimeDays),
        };
    }

    private static ErrorOr<ApplicationDcrOverrides> MapDcr(ApplicationDcrDto d)
    {
        if (LifetimeError("Dcr", d.AccessTokenLifetimeMinutes, d.RefreshTokenLifetimeDays) is { } e) return e;
        return new ApplicationDcrOverrides
        {
            Enabled = d.Enabled,
            AccessTokenLifetime = Minutes(d.AccessTokenLifetimeMinutes),
            RefreshTokenLifetime = Days(d.RefreshTokenLifetimeDays),
            GcTtlDays = d.GcTtlDays,
            PerIpRateLimitPerHour = d.PerIpRateLimitPerHour,
            PerRealmRateLimitPerDay = d.PerRealmRateLimitPerDay,
            ReservedNames = d.ReservedNames,
        };
    }

    private static ErrorOr<ApplicationCimdOverrides> MapCimd(ApplicationCimdDto d)
    {
        if (LifetimeError("Cimd", d.AccessTokenLifetimeMinutes, d.RefreshTokenLifetimeDays) is { } e) return e;
        return new ApplicationCimdOverrides
        {
            Enabled = d.Enabled,
            AccessTokenLifetime = Minutes(d.AccessTokenLifetimeMinutes),
            RefreshTokenLifetime = Days(d.RefreshTokenLifetimeDays),
        };
    }

    private static ErrorOr<ApplicationRegistrationFieldsOverrides> MapRegistrationFields(ApplicationRegistrationFieldsDto d)
    {
        var username = ParseRequirement("Username", d.Username);
        if (username.IsError) return username.FirstError;
        var firstname = ParseRequirement("Firstname", d.Firstname);
        if (firstname.IsError) return firstname.FirstError;
        var lastname = ParseRequirement("Lastname", d.Lastname);
        if (lastname.IsError) return lastname.FirstError;

        return new ApplicationRegistrationFieldsOverrides
        {
            Username = username.Value,
            Firstname = firstname.Value,
            Lastname = lastname.Value,
        };
    }

    private static ErrorOr<FieldRequirement?> ParseRequirement(string field, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (FieldRequirement?)null;
        if (!Enum.TryParse<FieldRequirement>(raw, ignoreCase: true, out var v))
            return Error.Validation($"RegistrationFields.Invalid{field}",
                $"{field} must be Off, Optional or Required.");
        return (FieldRequirement?)v;
    }

    private static ErrorOr<BrandingSettings> MapBranding(ApplicationBrandingDto d)
    {
        var color = NullIfBlank(d.PrimaryColor);
        if (color is not null && !CssColorRegex.IsMatch(color.Trim()))
            return Error.Validation("Application.InvalidPrimaryColor", "PrimaryColor must be a valid CSS color.");

        Guid? logo = null, favicon = null;
        if (!string.IsNullOrWhiteSpace(d.LogoAssetId))
        {
            if (!ShortGuid.TryDecode(d.LogoAssetId, out var l))
                return Error.Validation("Application.InvalidAssetId", "LogoAssetId must be a ShortGuid.");
            logo = l;
        }
        if (!string.IsNullOrWhiteSpace(d.FaviconAssetId))
        {
            if (!ShortGuid.TryDecode(d.FaviconAssetId, out var f))
                return Error.Validation("Application.InvalidAssetId", "FaviconAssetId must be a ShortGuid.");
            favicon = f;
        }

        return new BrandingSettings
        {
            ProductName = NullIfBlank(d.ProductName),
            PrimaryColor = color,
            LogoAssetId = logo,
            FaviconAssetId = favicon,
        };
    }

    private static Error? LifetimeError(string section, int? accessMinutes, int? refreshDays)
    {
        if (accessMinutes is { } a && (a < 1 || a > 60))
            return Error.Validation($"{section}.InvalidAccessTokenLifetime", "AccessTokenLifetimeMinutes must be 1–60.");
        if (refreshDays is { } r && (r < 1 || r > 30))
            return Error.Validation($"{section}.InvalidRefreshTokenLifetime", "RefreshTokenLifetimeDays must be 1–30.");
        return null;
    }

    private static TimeSpan? Minutes(int? m) => m is { } v ? TimeSpan.FromMinutes(v) : null;
    private static TimeSpan? Days(int? d) => d is { } v ? TimeSpan.FromDays(v) : null;
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ── Mapping (domain → DTO) ──────────────────────────────────────────────

    internal static ApplicationSettingsDto ToDto(ApplicationSettings? doc)
    {
        if (doc is null) return new ApplicationSettingsDto();
        return new ApplicationSettingsDto
        {
            Origin = doc.Origin is null ? null : new ApplicationOriginDto { Subdomain = doc.Origin.Subdomain },
            Branding = doc.Branding is null ? null : new ApplicationBrandingDto
            {
                ProductName = doc.Branding.ProductName,
                PrimaryColor = doc.Branding.PrimaryColor,
                LogoAssetId = doc.Branding.LogoAssetId is { } l ? ShortGuid.Encode(l) : null,
                LogoUrl = doc.Branding.LogoAssetId is { } lu ? $"/api/assets/{ShortGuid.Encode(lu)}" : null,
                FaviconAssetId = doc.Branding.FaviconAssetId is { } f ? ShortGuid.Encode(f) : null,
                FaviconUrl = doc.Branding.FaviconAssetId is { } fu ? $"/api/assets/{ShortGuid.Encode(fu)}" : null,
            },
            EmailBranding = doc.EmailBranding is null ? null
                : new ApplicationEmailBrandingDto { ProductName = doc.EmailBranding.ProductName },
            SelfRegistration = doc.SelfRegistration is null ? null : new ApplicationSelfRegistrationDto
            {
                Posture = doc.SelfRegistration.Posture?.ToString(),
                Enabled = doc.SelfRegistration.Enabled,
                RequireEmailVerification = doc.SelfRegistration.RequireEmailVerification,
                AllowedEmailDomains = doc.SelfRegistration.AllowedEmailDomains,
                RequireAdminApproval = doc.SelfRegistration.RequireAdminApproval,
                DefaultGroupIds = doc.SelfRegistration.DefaultGroupIds,
                TermsOfServiceUrl = doc.SelfRegistration.TermsOfServiceUrl,
                PrivacyPolicyUrl = doc.SelfRegistration.PrivacyPolicyUrl,
            },
            NativeGrants = doc.NativeGrants is null ? null : new ApplicationNativeGrantsDto
            {
                Enabled = doc.NativeGrants.Enabled,
                AccessTokenLifetimeMinutes = doc.NativeGrants.AccessTokenLifetime is { } na ? (int)na.TotalMinutes : null,
                RefreshTokenLifetimeDays = doc.NativeGrants.RefreshTokenLifetime is { } nr ? (int)nr.TotalDays : null,
            },
            ClientSessions = doc.ClientSessions is null ? null : new ApplicationClientSessionsDto
            {
                IdleLifetimeDays = doc.ClientSessions.IdleLifetime is { } idle ? (int)idle.TotalDays : null,
                AbsoluteLifetimeDays = doc.ClientSessions.AbsoluteLifetime is { } absolute ? (int)absolute.TotalDays : null,
            },
            Dcr = doc.Dcr is null ? null : new ApplicationDcrDto
            {
                Enabled = doc.Dcr.Enabled,
                AccessTokenLifetimeMinutes = doc.Dcr.AccessTokenLifetime is { } da ? (int)da.TotalMinutes : null,
                RefreshTokenLifetimeDays = doc.Dcr.RefreshTokenLifetime is { } dr ? (int)dr.TotalDays : null,
                GcTtlDays = doc.Dcr.GcTtlDays,
                PerIpRateLimitPerHour = doc.Dcr.PerIpRateLimitPerHour,
                PerRealmRateLimitPerDay = doc.Dcr.PerRealmRateLimitPerDay,
                ReservedNames = doc.Dcr.ReservedNames,
            },
            Cimd = doc.Cimd is null ? null : new ApplicationCimdDto
            {
                Enabled = doc.Cimd.Enabled,
                AccessTokenLifetimeMinutes = doc.Cimd.AccessTokenLifetime is { } ca ? (int)ca.TotalMinutes : null,
                RefreshTokenLifetimeDays = doc.Cimd.RefreshTokenLifetime is { } cr ? (int)cr.TotalDays : null,
            },
            RegistrationFields = doc.RegistrationFields is null ? null : new ApplicationRegistrationFieldsDto
            {
                Username = doc.RegistrationFields.Username?.ToString(),
                Firstname = doc.RegistrationFields.Firstname?.ToString(),
                Lastname = doc.RegistrationFields.Lastname?.ToString(),
            },
        };
    }
}
