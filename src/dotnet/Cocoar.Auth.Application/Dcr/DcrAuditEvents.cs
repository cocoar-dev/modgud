namespace Cocoar.Auth.Application.Dcr;

/// <summary>
/// Canonical event-name strings used in <c>Auth: DCR …</c> log lines.
/// Centralised so both the emitting site (registration endpoint, GC
/// service, token-issue handler) and the consuming site (SPA auth-log
/// grid filter chip) reference the same vocabulary.
///
/// <para>The auth-log capture path is message-prefix-based (see
/// <c>AuthLogSink</c>); the SPA filters by matching the prefix
/// "DCR " + the event name. Renaming an event name without
/// updating both sides breaks the filter UI — that's why these live
/// here, not as inline literals.</para>
///
/// <para>Reasons (for Rejected / RateLimitTriggered) ride in a
/// <c>{Reason}</c> Serilog property using the
/// <see cref="DcrRejectionReason"/> enum names — see
/// <c>DcrRegistrationEndpoints.cs</c> for the emission pattern.</para>
/// </summary>
public static class DcrAuditEvents
{
    /// <summary>Successful registration. Fields: IP, Realm,
    /// ClientId, ClientName.</summary>
    public const string ClientRegistered = "DCR client registered";

    /// <summary>Validation rejected the registration request. Fields:
    /// IP, Reason ({Reason}={RejectionReason}), ClientName.</summary>
    public const string RegistrationRejected = "DCR registration rejected";

    /// <summary>Per-IP or per-realm rate-limit hit. Fields: IP,
    /// Reason ({Reason}=PerIpRateLimit | PerRealmRateLimit).</summary>
    public const string RateLimitTriggered = "DCR rate-limit triggered";

    /// <summary>First successful <c>/connect/authorize</c> invocation
    /// for a freshly-registered DCR client. The cleanest signal for
    /// "registration was real, not bot-noise". Emitted by the
    /// LastUsedAt-update path (lands with the GC infra in a follow-up
    /// commit). Fields: ClientId, RegisteredAt.</summary>
    public const string ClientFirstUsed = "DCR client first used";

    /// <summary>GC sweep soft-deleted a DCR client whose
    /// <c>LastUsedAt</c> aged past the per-realm TTL. Fields: ClientId,
    /// RegisteredAt, LastUsedAt, TtlDays. Emitted by the GC
    /// IHostedService (follow-up commit).</summary>
    public const string ClientGarbageCollected = "DCR client garbage collected";
}
