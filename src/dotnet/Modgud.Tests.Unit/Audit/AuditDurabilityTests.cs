using Modgud.Infrastructure.Audit;

namespace Modgud.Tests.Unit.Audit;

public class AuditDurabilityTests
{
    public static TheoryData<string, AuditDurabilityClass> ClassifiedEvents => new()
    {
        { AuditEvents.RefreshTokenReuseDetected, AuditDurabilityClass.Required },
        { AuditEvents.AuditLogExported, AuditDurabilityClass.Required },
        { AuditEvents.SecurityRetentionChanged, AuditDurabilityClass.Required },
        { AuditEvents.SigningKeyRotated, AuditDurabilityClass.Required },
        { AuditEvents.SamlCertRotated, AuditDurabilityClass.Required },
        { AuditEvents.SamlSigningCertificatesChanged, AuditDurabilityClass.Required },
        { AuditEvents.RecoveryCliInvoked, AuditDurabilityClass.Required },
        { AuditEvents.RealmProvisioned, AuditDurabilityClass.Required },
        { AuditEvents.RealmAdopted, AuditDurabilityClass.Required },
        { AuditEvents.ControlPlaneTransferred, AuditDurabilityClass.Required },
        { AuditEvents.ControlPlaneRealmOperation, AuditDurabilityClass.Required },
        { AuditEvents.BootstrapInviteIssued, AuditDurabilityClass.Required },
        { AuditEvents.DcrClientRegistered, AuditDurabilityClass.Required },

        { AuditEvents.ExternalLoginProtocolRejected, AuditDurabilityClass.Incident },
        { AuditEvents.SamlSignatureRejected, AuditDurabilityClass.Incident },
        { AuditEvents.IdentityHijackBlocked, AuditDurabilityClass.Incident },
        { AuditEvents.JitEmailConflict, AuditDurabilityClass.Incident },
        { AuditEvents.PrivilegeEscalationBlocked, AuditDurabilityClass.Incident },

        { AuditEvents.LoginFailed, AuditDurabilityClass.Abuse },
        { AuditEvents.LoginFailedUnknownUser, AuditDurabilityClass.Abuse },
        { AuditEvents.MagicLinkInvalid, AuditDurabilityClass.Abuse },
        { AuditEvents.ExternalLoginPolicyRejected, AuditDurabilityClass.Abuse },
        { AuditEvents.RateLimitTriggered, AuditDurabilityClass.Abuse },
        { AuditEvents.DcrRegistrationRejected, AuditDurabilityClass.Abuse },
        { AuditEvents.BootstrapInviteRejected, AuditDurabilityClass.Abuse },

        { AuditEvents.ExternalLoginConfigurationError, AuditDurabilityClass.Telemetry },
        { AuditEvents.SigningKeyPurged, AuditDurabilityClass.Telemetry },
        { AuditEvents.SamlMetadataRefreshCompleted, AuditDurabilityClass.Telemetry },
        { AuditEvents.AccountLifecycleSwept, AuditDurabilityClass.Telemetry },
        { AuditEvents.DcrClientFirstUsed, AuditDurabilityClass.Telemetry },
        { AuditEvents.DcrClientGarbageCollected, AuditDurabilityClass.Telemetry },
    };

    [Theory]
    [MemberData(nameof(ClassifiedEvents))]
    public void Streamless_events_have_an_explicit_delivery_contract(
        string eventType,
        AuditDurabilityClass expected)
    {
        Assert.Equal(expected, AuditDurability.Classify(eventType));
    }

    [Fact]
    public void Unknown_event_cannot_silently_choose_a_weaker_contract()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AuditDurability.Classify("security.unclassified"));
    }
}
