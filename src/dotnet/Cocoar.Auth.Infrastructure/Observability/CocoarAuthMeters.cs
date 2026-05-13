using System.Diagnostics.Metrics;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;

namespace Cocoar.Auth.Infrastructure.Observability;

/// <summary>
/// Central OpenTelemetry meters for Cocoar.Auth domain events. Picked up
/// by the OTel wiring via <c>.AddMeter(CocoarAuthMeters.Name)</c>, so any
/// project that references Infrastructure can record without needing OTel
/// in its own dependency graph.
///
/// <para>Realm tag is pulled from <see cref="TenantContext.Current"/> by
/// default — works for HTTP requests (set by <c>RealmMiddleware</c>) and
/// background services (set via <c>TenantContext.Enter</c>). Callers may
/// pass an explicit realm for the few sites that operate outside any
/// tenant scope (e.g. realm provisioning runs against the system tenant).</para>
///
/// <para>Tag cardinality is bounded by design: realm count stays in the
/// dozens, all other tag values are drawn from constant sets defined on
/// this class. No user-controlled strings ever land in a tag.</para>
/// </summary>
public static class CocoarAuthMeters
{
    public const string Name = "Cocoar.Auth";

    public static readonly Meter Meter = new(Name);

    /// <summary>
    /// In-memory ring buffer for the in-app live view (Phase 5). Set once
    /// at DI bootstrap; left null when the API host doesn't wire it (unit
    /// tests, recovery-CLI, …) so Record* stays a pure OTel emission.
    /// </summary>
    public static ObservabilityActivityBuffer? ActivityBuffer { get; set; }

    // Login methods — bounded set, callers use the constants below.
    public static class LoginMethod
    {
        public const string Password = "password";
        public const string MagicLink = "magic_link";
        public const string Passkey = "passkey";
        public const string Mfa = "mfa";
        public const string EmailOtp = "email_otp";
        public const string External = "external";
    }

    public static class LoginOutcome
    {
        public const string Success = "success";
        public const string Failure = "failure";
        public const string Locked = "locked";
        public const string TwoFactorRequired = "2fa_required";
        public const string RequiresSetup = "requires_setup";
    }

    public static class ClientType
    {
        public const string Confidential = "confidential";
        public const string Public = "public";
        public const string Dcr = "dcr";
    }

    public static class DcrOutcome
    {
        public const string Success = "success";
        public const string RateLimited = "rate_limited";
        public const string PolicyDenied = "policy_denied";
        public const string InvalidRequest = "invalid_request";
    }

    public static class DcrRateLimitScope
    {
        public const string Realm = "realm";
        public const string Client = "client";
    }

    public static class GdprRequestType
    {
        public const string Export = "export";
        public const string Delete = "delete";
        public const string Mask = "mask";
    }

    private static readonly Counter<long> _logins = Meter.CreateCounter<long>(
        "cocoar_auth.logins.total",
        unit: "{login}",
        description: "Login attempts by realm, method, and outcome.");

    private static readonly Counter<long> _tokenMinted = Meter.CreateCounter<long>(
        "cocoar_auth.token.minted.total",
        unit: "{token}",
        description: "OAuth/OIDC tokens minted by realm, grant type, and client type.");

    // Refresh-token rejection proxy. OpenIddict 7 doesn't expose a dedicated
    // event for reuse-detection specifically; we count invalid_grant on
    // refresh_token, which under strict reuse-leeway=0 is dominated by reuse.
    // Expired/revoked tokens contribute too, so unusual spikes (not a steady
    // baseline) are the signal worth alerting on.
    private static readonly Counter<long> _refreshRejected = Meter.CreateCounter<long>(
        "cocoar_auth.token.refresh.rejected.total",
        unit: "{rejection}",
        description: "Refresh-token grant rejected (reuse-detected | expired | revoked).");

    private static readonly Counter<long> _twoFactorBlocked = Meter.CreateCounter<long>(
        "cocoar_auth.two_factor.enforcement.blocked.total",
        unit: "{request}",
        description: "Requests blocked by 2FA enforcement after grace expiry.");

    private static readonly Counter<long> _dcrRegistration = Meter.CreateCounter<long>(
        "cocoar_auth.dcr.registration.total",
        unit: "{registration}",
        description: "Dynamic client registration attempts by realm and outcome.");

    private static readonly Counter<long> _dcrRateLimit = Meter.CreateCounter<long>(
        "cocoar_auth.dcr.rate_limit.hit.total",
        unit: "{hit}",
        description: "DCR rate-limit hits by realm and scope.");

    private static readonly Counter<long> _realmProvisioned = Meter.CreateCounter<long>(
        "cocoar_auth.realm.provisioned.total",
        unit: "{realm}",
        description: "Realms provisioned.");

    private static readonly Counter<long> _gdprRequest = Meter.CreateCounter<long>(
        "cocoar_auth.gdpr.request.total",
        unit: "{request}",
        description: "GDPR self-service requests by type.");

    public static void RecordLogin(string method, string outcome, string? realm = null)
    {
        var r = realm ?? TenantContext.Current;
        _logins.Add(1,
            new KeyValuePair<string, object?>("realm", r),
            new KeyValuePair<string, object?>("method", method),
            new KeyValuePair<string, object?>("outcome", outcome));
        ActivityBuffer?.Record(ObservabilityEventTypes.Login, r,
            new Dictionary<string, string> { ["method"] = method, ["outcome"] = outcome });
    }

    public static void RecordTokenMinted(string grantType, string clientType, string? realm = null)
    {
        var r = realm ?? TenantContext.Current;
        _tokenMinted.Add(1,
            new KeyValuePair<string, object?>("realm", r),
            new KeyValuePair<string, object?>("grant_type", grantType),
            new KeyValuePair<string, object?>("client_type", clientType));
        ActivityBuffer?.Record(ObservabilityEventTypes.TokenMinted, r,
            new Dictionary<string, string> { ["grant_type"] = grantType, ["client_type"] = clientType });
    }

    public static void RecordRefreshRejected(string? realm = null)
    {
        var r = realm ?? TenantContext.Current;
        _refreshRejected.Add(1, new KeyValuePair<string, object?>("realm", r));
        ActivityBuffer?.Record(ObservabilityEventTypes.TokenRefreshRejected, r);
    }

    public static void RecordTwoFactorBlocked(string? realm = null)
    {
        var r = realm ?? TenantContext.Current;
        _twoFactorBlocked.Add(1, new KeyValuePair<string, object?>("realm", r));
        ActivityBuffer?.Record(ObservabilityEventTypes.TwoFactorBlocked, r);
    }

    public static void RecordDcrRegistration(string outcome, string? realm = null)
    {
        var r = realm ?? TenantContext.Current;
        _dcrRegistration.Add(1,
            new KeyValuePair<string, object?>("realm", r),
            new KeyValuePair<string, object?>("outcome", outcome));
        ActivityBuffer?.Record(ObservabilityEventTypes.DcrRegistration, r,
            new Dictionary<string, string> { ["outcome"] = outcome });
    }

    public static void RecordDcrRateLimitHit(string scope, string? realm = null)
    {
        var r = realm ?? TenantContext.Current;
        _dcrRateLimit.Add(1,
            new KeyValuePair<string, object?>("realm", r),
            new KeyValuePair<string, object?>("scope", scope));
        ActivityBuffer?.Record(ObservabilityEventTypes.DcrRateLimitHit, r,
            new Dictionary<string, string> { ["scope"] = scope });
    }

    public static void RecordRealmProvisioned()
    {
        _realmProvisioned.Add(1);
        ActivityBuffer?.Record(ObservabilityEventTypes.RealmProvisioned, TenantContext.Current);
    }

    public static void RecordGdprRequest(string type, string? realm = null)
    {
        var r = realm ?? TenantContext.Current;
        _gdprRequest.Add(1,
            new KeyValuePair<string, object?>("realm", r),
            new KeyValuePair<string, object?>("type", type));
        ActivityBuffer?.Record(ObservabilityEventTypes.GdprRequest, r,
            new Dictionary<string, string> { ["type"] = type });
    }
}
