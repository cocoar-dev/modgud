using System.Text.Json;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.RealmSettings;
using Modgud.Infrastructure.Persistence.Marten.Projections.PositionTerminals;

namespace Modgud.Tests.Unit.Authorization;

public class PositionTerminalSecurityContractTests
{
    [Fact]
    public void Open_id_sets_and_capability_arrays_round_trip_without_enum_ordinals()
    {
        var policy = new PositionTerminalPolicy
        {
            Enabled = true,
            AllowedActivationProofs = [ActivationProofMethodIds.PersonalPasskey],
            AllowedDeviceBindings = [DeviceBindingIds.Dpop],
        };
        var policyJson = JsonSerializer.Serialize(policy);
        var restoredPolicy = JsonSerializer.Deserialize<PositionTerminalPolicy>(policyJson)!;
        Assert.Equal(policy.AllowedActivationProofs, restoredPolicy.AllowedActivationProofs);
        Assert.Equal(policy.AllowedDeviceBindings, restoredPolicy.AllowedDeviceBindings);

        var floor = new PositionSecuritySettings
        {
            RequiredProofCapabilities = ProofCapability.IdentifiedActor |
                                        ProofCapability.PhishingResistant,
            RequiredBindingCapabilities = BindingCapability.DeviceIdentity |
                                          BindingCapability.SenderConstrained,
        };
        var json = JsonSerializer.Serialize(floor);
        Assert.Contains("[\"IdentifiedActor\",\"PhishingResistant\"]", json);
        Assert.Contains("[\"DeviceIdentity\",\"SenderConstrained\"]", json);
        Assert.Equal(floor, JsonSerializer.Deserialize<PositionSecuritySettings>(json));
    }

    [Fact]
    public void Capability_floor_is_set_based_and_not_a_numeric_binding_order()
    {
        Assert.True(PositionTerminalSecurity.BindingMeetsFloor(
            DeviceBindingIds.Dpop,
            BindingCapability.DeviceIdentity | BindingCapability.SenderConstrained));
        Assert.False(PositionTerminalSecurity.BindingMeetsFloor(
            DeviceBindingIds.ClientSecret,
            BindingCapability.SenderConstrained));
        Assert.False(PositionTerminalSecurity.BindingMeetsFloor(
            DeviceBindingIds.None,
            BindingCapability.DeviceIdentity));
        Assert.True(PositionTerminalSecurity.ProofMeetsFloor(
            ActivationProofMethodIds.PersonalPasskey,
            ProofCapability.IdentifiedActor |
            ProofCapability.PhishingResistant |
            ProofCapability.IndividuallyRevocable));
        Assert.False(PositionTerminalSecurity.ProofMeetsFloor(
            "removed-plugin", ProofCapability.None));
    }

    [Fact]
    public void Legacy_events_upcast_to_personal_passkey_and_dpop()
    {
        var terminalId = Guid.NewGuid();
        var terminalJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Id"] = terminalId,
            ["PositionPrincipalId"] = Guid.NewGuid(),
            ["DisplayName"] = "Legacy terminal",
            ["Location"] = null,
            ["OAuthApplicationId"] = Guid.NewGuid(),
            ["ClientId"] = "legacy.terminal",
            ["WebAuthnRpId"] = "example.test",
            ["CreatedByUserId"] = Guid.NewGuid(),
            ["CreatedAt"] = DateTimeOffset.UtcNow,
        });
        var legacyTerminalEvent = JsonSerializer.Deserialize<TerminalEnrollmentCreated>(terminalJson)!;
        Assert.Null(legacyTerminalEvent.Binding);
        var terminal = new TerminalEnrollmentProjection().Create(legacyTerminalEvent);
        Assert.Equal(DeviceBindingIds.Dpop, terminal.Binding);

        var userId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var legacySessionEvent = new StaffingSessionStarted(
            Guid.NewGuid(), Guid.NewGuid(), terminalId, userId, credentialId, grantId,
            "legacy-jkt", "legacy-auth", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(16));
        var staffing = new StaffingSessionProjection().Create(legacySessionEvent);
        Assert.Equal(ActivationProofMethodIds.PersonalPasskey, staffing.Evidence.MethodId);
        Assert.Equal(DeviceBindingIds.Dpop, staffing.Evidence.Binding);
        Assert.Equal(userId, staffing.Evidence.UserId);
        Assert.Equal(credentialId, staffing.Evidence.CredentialId);
        Assert.Equal(grantId, staffing.Evidence.GrantId);
    }
}
