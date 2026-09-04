namespace Modgud.Authentication.BackChannelLogout;

/// <summary>ADR 0009 — shared names of the back-channel logout transport.</summary>
public static class BackChannelLogoutConstants
{
    /// <summary>OpenID Connect Back-Channel Logout 1.0 §2.4 — the <c>events</c> member key.</summary>
    public const string EventUri = "http://schemas.openid.net/event/backchannel-logout";

    /// <summary>Named <c>HttpClient</c> the delivery worker uses (SSRF-guarded primary handler).</summary>
    public const string HttpClientName = "BackChannelLogout";

    /// <summary><c>HttpContext.Items</c> key the end-session endpoint sets before signing the
    /// cookie out, so the session end names the relying party that asked for it (which
    /// is then not notified about its own logout).</summary>
    public const string InitiatingClientItem = "Modgud.BackChannelLogout.InitiatingClientId";

    /// <summary>Name of the event-store subscription that fans session ends out.</summary>
    public const string SubscriptionName = "backchannel-logout";

    /// <summary>Logout tokens are short-lived by design (spec: a few minutes at most).</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Per-attempt HTTP timeout.</summary>
    public static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Delay before the retry that follows the n-th failed attempt (the first
    /// attempt is immediate). After the last entry the delivery is given up; the change
    /// feed carries the same fact. The per-realm retry job runs every minute, so the
    /// first step is minute-granular.</summary>
    public static readonly TimeSpan[] RetrySchedule =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
    ];
}
