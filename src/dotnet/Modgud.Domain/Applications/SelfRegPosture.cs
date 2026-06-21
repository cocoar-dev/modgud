namespace Modgud.Domain.Applications;

/// <summary>
/// Per-Application self-registration posture (ADR-0011, OQ3 / D6). Decides
/// how a passwordless sign-up is triggered for an Application. Resolved by
/// <see cref="EffectiveSettings"/>; the behaviour itself is wired in Phase 5
/// (native passwordless registration) — until then this is inert config.
///
/// <para>The default for an Application is <see cref="JitOnOtp"/> (the
/// consumer / native-app default; the Slack/Notion email-code pattern). A
/// request with no Application in context resolves a <c>null</c> posture and
/// keeps the legacy realm-only registration behaviour.</para>
/// </summary>
public enum SelfRegPosture
{
    /// <summary>No self-registration for this Application. An unknown email at
    /// the native OTP-request endpoint gets the uniform anti-enumeration
    /// response without creating a user.</summary>
    Off,

    /// <summary>Sign-in-or-sign-up: an unknown email creates a passwordless
    /// user and sends the OTP; redeeming it both verifies the mailbox and
    /// signs in. Lowest friction — the consumer default.</summary>
    JitOnOtp,

    /// <summary>Registration is a deliberate, separate step (room for ToS /
    /// profile fields); sign-in stays strict (known users only).</summary>
    ExplicitEndpoint,
}
