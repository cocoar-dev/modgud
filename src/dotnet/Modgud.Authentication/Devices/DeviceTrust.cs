using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Devices;

/// <summary>
/// ADR 0008 — reads, issues and sweeps the device cookie and its
/// <see cref="TrustedDevice"/> record.
/// </summary>
public interface IDeviceTrust
{
    /// <summary>The device id carried by a valid <c>Modgud.Device</c> cookie, or null
    /// (no cookie, tampered, foreign realm). Never throws.</summary>
    Guid? ReadDeviceId(HttpContext http);

    /// <summary>Is the device trusted for this user, i.e. did the user complete a login
    /// from it before?</summary>
    Task<bool> IsTrustedAsync(Guid deviceId, Guid userId, CancellationToken ct = default);

    /// <summary>After a successful interactive login: record the user on the device
    /// (creating it when the browser has no cookie yet) and (re)issue the cookie.
    /// Returns the device id.</summary>
    Task<Guid> IssueAsync(HttpContext http, Guid userId, CancellationToken ct = default);

    /// <summary>Remove a user from every device they are listed on (GDPR erasure).
    /// Devices left without users are deleted. Returns the number touched.</summary>
    Task<int> ForgetUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Hard-delete devices idle since <paramref name="idleBefore"/>. Returns the count.</summary>
    Task<int> SweepAsync(DateTimeOffset idleBefore, CancellationToken ct = default);
}

public sealed class DeviceTrust(
    IDocumentSession session,
    IDataProtectionProvider dataProtection,
    TimeProvider clock,
    ILogger<DeviceTrust> logger) : IDeviceTrust
{
    private const string Purpose = "Modgud.Device.v1";

    public Guid? ReadDeviceId(HttpContext http)
    {
        var raw = http.Request.Cookies[TrustedDevice.CookieName];
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var payload = Protector().Unprotect(raw);
            // "<realm>|<guid>" — the realm pins the cookie to the tenant it was issued
            // for, on top of the tenant-scoped protection key.
            var sep = payload.IndexOf('|');
            if (sep <= 0) return null;
            if (!string.Equals(payload[..sep], TenantContext.Current, StringComparison.Ordinal)) return null;
            return Guid.TryParse(payload[(sep + 1)..], out var id) ? id : null;
        }
        catch
        {
            // Tampered, expired key ring, cookie from another deployment: no device.
            return null;
        }
    }

    public async Task<bool> IsTrustedAsync(Guid deviceId, Guid userId, CancellationToken ct = default)
    {
        var device = await session.LoadAsync<TrustedDevice>(deviceId, ct);
        return device is not null && device.IsTrustedFor(userId);
    }

    public async Task<Guid> IssueAsync(HttpContext http, Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var existingId = ReadDeviceId(http);
        TrustedDevice? device = existingId is { } id ? await session.LoadAsync<TrustedDevice>(id, ct) : null;
        if (device is null)
        {
            device = new TrustedDevice { Id = Guid.NewGuid(), CreatedAt = now, LastSeenAt = now };
        }
        device.Touch(userId, now);
        session.Store(device);
        await session.SaveChangesAsync(ct);

        WriteCookie(http, device.Id, now);
        logger.LogDebug("Device {DeviceId} trusted for user {UserId}", device.Id, userId);
        return device.Id;
    }

    public async Task<int> ForgetUserAsync(Guid userId, CancellationToken ct = default)
    {
        var devices = await session.Query<TrustedDevice>()
            .Where(d => d.UserIds.Contains(userId))
            .ToListAsync(ct);
        foreach (var device in devices)
        {
            device.UserIds.Remove(userId);
            if (device.UserIds.Count == 0) session.Delete(device);
            else session.Store(device);
        }
        if (devices.Count > 0) await session.SaveChangesAsync(ct);
        return devices.Count;
    }

    public async Task<int> SweepAsync(DateTimeOffset idleBefore, CancellationToken ct = default)
    {
        var stale = await session.Query<TrustedDevice>()
            .Where(d => d.LastSeenAt < idleBefore)
            .Select(d => d.Id)
            .ToListAsync(ct);
        foreach (var id in stale) session.Delete<TrustedDevice>(id);
        if (stale.Count > 0) await session.SaveChangesAsync(ct);
        return stale.Count;
    }

    private IDataProtector Protector() => dataProtection.CreateProtector(Purpose);

    private void WriteCookie(HttpContext http, Guid deviceId, DateTimeOffset now)
    {
        var value = Protector().Protect($"{TenantContext.Current}|{deviceId:N}");
        var options = new CookieOptions
        {
            HttpOnly = true,
            // Same policy as the auth cookie: Secure whenever the (forwarded) request
            // was HTTPS, plain for the local Vite proxy.
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            Expires = now + TrustedDevice.IdleLifetime,
        };
        // Mirror TenantApexCookieManager: widen to the realm's primary domain when the
        // request is on it or on an App subdomain, so the device is recognised across
        // the tenant's hosts; host-only otherwise.
        if (http.Items[TenantConstants.HttpContextTenantInfoKey] is TenantInfo tenant
            && CookieDomainFor(http.Request.Host.Host, tenant.PrimaryDomain) is { } domain)
        {
            options.Domain = domain;
        }
        http.Response.Cookies.Append(TrustedDevice.CookieName, value, options);
    }

    internal static string? CookieDomainFor(string? host, string? primaryDomain)
    {
        var h = host?.Trim();
        var primary = primaryDomain?.Trim();
        if (string.IsNullOrEmpty(h) || string.IsNullOrEmpty(primary)) return null;
        return h.Equals(primary, StringComparison.OrdinalIgnoreCase)
               || h.EndsWith("." + primary, StringComparison.OrdinalIgnoreCase)
            ? primary
            : null;
    }
}
