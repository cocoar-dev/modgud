using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modgud.Authentication.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.RateLimiting;
using OpenIddict.Abstractions;

namespace Modgud.Authentication.RateLimiting;

/// <summary>Outcome of building the caller context: the context, or a 400 to return.</summary>
public sealed record AuthCallerContextResult(AuthCallerContext? Context, string? ErrorCode, string? ErrorDetail)
{
    public bool IsError => Context is null;
}

public interface IAuthCallerContextFactory
{
    Task<AuthCallerContextResult> BuildAsync(HttpContext http, CancellationToken ct = default);
}

/// <summary>
/// ADR 0007 — resolves who is calling. Client authentication is read from
/// <c>client_secret_basic</c> (<c>Authorization: Basic</c>); the dedicated
/// <c>Modgud-Forwarded-For</c> header is honoured ONLY for a confidential client that
/// authenticated on this request and holds <c>cap:trusted-forwarder</c>. A forwarded
/// header without an entitled client, or an entitled client without the header, is a
/// 400 — both independent of any target identifier, so they leak nothing.
///
/// <para>The Testing environment keeps the long-standing partition hack: the
/// WebApplicationFactory leaves the remote address null, so each request gets its own
/// source key unless it opts into a shared one via <c>X-Test-RateLimit</c>. A forwarded
/// address always wins over that, so forwarder tests see real keys.</para>
/// </summary>
public sealed class AuthCallerContextFactory(IHostEnvironment env) : IAuthCallerContextFactory
{
    // The OpenIddict manager and the settings resolver both open a tenant-scoped Marten
    // session on construction, which the realm-independent installation and bootstrap
    // endpoints (zero-realm cold start) cannot provide. Resolve them only on the paths
    // that actually need a realm, from the request's own scope.
    public const string ErrorForwarderNotTrusted = "Auth.ForwarderNotTrusted";
    public const string ErrorForwardedAddressRequired = "Auth.ForwardedAddressRequired";
    public const string TestPartitionHeader = "X-Test-RateLimit";

    public async Task<AuthCallerContextResult> BuildAsync(HttpContext http, CancellationToken ct = default)
    {
        var realmSlug = http.Items[TenantConstants.HttpContextTenantIdKey] as string;
        var applicationId = http.GetApplicationId();
        var remote = http.Connection.RemoteIpAddress;

        // ── client authentication (client_secret_basic) ─────────────────────
        string? clientId = null;
        var confidential = false;
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        if (TryReadBasicCredentials(http, out var presentedId, out var presentedSecret) && realmSlug is not null)
        {
            var applications = http.RequestServices.GetRequiredService<IOpenIddictApplicationManager>();
            var app = await applications.FindByClientIdAsync(presentedId, ct);
            if (app is not null
                && await applications.HasClientTypeAsync(app, OpenIddictConstants.ClientTypes.Confidential, ct)
                && await applications.ValidateClientSecretAsync(app, presentedSecret, ct))
            {
                clientId = presentedId;
                confidential = true;
                foreach (var permission in await applications.GetPermissionsAsync(app, ct))
                {
                    if (permission.StartsWith(OAuthPermissions.Prefixes.Capability, StringComparison.Ordinal))
                        capabilities.Add(permission);
                }
            }
        }

        // ── forwarded address (dedicated header, capability-gated) ──────────
        IPAddress? forwarded = null;
        var header = http.Request.Headers[AuthCallerContext.ForwardedForHeader].ToString();
        var entitled = confidential && capabilities.Contains(OAuthPermissions.Capabilities.TrustedForwarder);
        if (!string.IsNullOrWhiteSpace(header))
        {
            if (!entitled)
                return new AuthCallerContextResult(null, ErrorForwarderNotTrusted,
                    "The forwarded-address header is only accepted from an authenticated confidential client with the trusted-forwarder capability.");
            if (!IPAddress.TryParse(header.Trim(), out forwarded))
                return new AuthCallerContextResult(null, ErrorForwardedAddressRequired,
                    "The forwarded address must be a single IPv4 or IPv6 literal without a port.");
        }
        else if (entitled)
        {
            return new AuthCallerContextResult(null, ErrorForwardedAddressRequired,
                "A trusted forwarder must send the end user's address in the Modgud-Forwarded-For header.");
        }

        // ── source key + allowlist ──────────────────────────────────────────
        var effective = forwarded ?? remote;
        string sourceKey;
        if (forwarded is not null)
            sourceKey = AuthCallerContext.SourceKeyFor(forwarded);
        else if (env.IsEnvironment("Testing"))
        {
            var shared = http.Request.Headers[TestPartitionHeader].ToString();
            sourceKey = string.IsNullOrEmpty(shared) ? Guid.NewGuid().ToString("N") : shared;
        }
        else
            sourceKey = remote is null ? "anon" : AuthCallerContext.SourceKeyFor(remote);

        var allowlisted = false;
        if (effective is not null && realmSlug is not null)
        {
            var settings = await TryResolveSettingsAsync(http, clientId, ct);
            allowlisted = IsAllowlisted(effective, AuthRateLimitSettings.EffectiveAllowlist(settings));
        }

        return new AuthCallerContextResult(new AuthCallerContext
        {
            RealmSlug = realmSlug,
            ApplicationId = applicationId,
            ClientId = clientId,
            ClientIsConfidential = confidential,
            ClientCapabilities = capabilities,
            RemoteAddress = remote,
            ForwardedAddress = forwarded,
            SourceKey = sourceKey,
            SourceAllowlisted = allowlisted,
        }, null, null);
    }

    private async Task<AuthRateLimitSettings?> TryResolveSettingsAsync(HttpContext http, string? clientId, CancellationToken ct)
    {
        try
        {
            if (http.Items[TenantConstants.HttpContextTenantIdKey] is not string { Length: > 0 }) return null;
            var settingsResolver = http.RequestServices.GetRequiredService<IApplicationSettingsResolver>();
            return (await settingsResolver.ResolveForRequestAsync(http, clientId, ct)).AuthRateLimits;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsAllowlisted(IPAddress address, IReadOnlyList<string> allowlist)
    {
        if (allowlist.Count == 0) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var entry in allowlist)
        {
            var text = entry.Trim();
            if (text.Length == 0) continue;
            if (IPNetwork.TryParse(text, out var network) && network.Contains(address)) return true;
            if (IPAddress.TryParse(text, out var single) && single.Equals(address)) return true;
        }
        return false;
    }

    private static bool TryReadBasicCredentials(HttpContext http, out string clientId, out string secret)
    {
        clientId = string.Empty;
        secret = string.Empty;
        var auth = http.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
            var idx = decoded.IndexOf(':');
            if (idx <= 0) return false;
            // RFC 6749 §2.3.1: both parts are form-urlencoded.
            clientId = Uri.UnescapeDataString(decoded[..idx]);
            secret = Uri.UnescapeDataString(decoded[(idx + 1)..]);
            return clientId.Length > 0 && secret.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
