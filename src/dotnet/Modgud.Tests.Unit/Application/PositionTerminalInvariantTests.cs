using ErrorOr;
using Modgud.Application.Services;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Tests.Unit.Application;

/// <summary>
/// MG-FT-03 — pins <c>ValidatePositionTerminalLinkInvariant</c> (plan §6.4):
/// the terminal client profile is public + secretless + DPoP + reference
/// tokens + RP-ID + exactly the three terminal grants, never SA-linked. Also
/// pins the fail-open trap the feasibility check found: the staffing grant IS
/// user-flow, so an SA-linked client can never carry it.
/// </summary>
public class PositionTerminalInvariantTests
{
    private static readonly string[] ValidGrants =
    [
        "urn:ietf:params:oauth:grant-type:device_code",
        "refresh_token",
        PositionGrantTypes.StaffingSession,
    ];

    private static Error? Validate(
        string[]? grants = null,
        string? clientType = OAuthClientTypes.Public,
        bool requireClientSecret = false,
        AccessTokenType accessTokenType = AccessTokenType.Reference,
        bool requireDpop = true,
        Guid? sa = null,
        Guid? fn = null,
        Guid? terminal = null,
        string? rpId = "alerthub.example")
        => OAuthAdminMapping.ValidatePositionTerminalLinkInvariant(
            grants ?? ValidGrants, clientType, requireClientSecret, accessTokenType,
            requireDpop, sa, fn, terminal, rpId);

    private static readonly Guid FnId = Guid.NewGuid();
    private static readonly Guid TerminalId = Guid.NewGuid();

    [Fact]
    public void A_non_terminal_client_is_not_checked()
        => Assert.Null(Validate(fn: null, terminal: null, grants: ["authorization_code"], requireDpop: false));

    [Fact]
    public void The_two_link_fields_must_be_set_together()
    {
        Assert.NotNull(Validate(fn: FnId, terminal: null));
        Assert.NotNull(Validate(fn: null, terminal: TerminalId));
    }

    [Fact]
    public void A_valid_terminal_profile_passes()
        => Assert.Null(Validate(fn: FnId, terminal: TerminalId));

    [Fact]
    public void Every_profile_rule_rejects_individually()
    {
        Assert.NotNull(Validate(fn: FnId, terminal: TerminalId, sa: Guid.NewGuid()));
        Assert.NotNull(Validate(fn: FnId, terminal: TerminalId, clientType: OAuthClientTypes.Confidential));
        Assert.NotNull(Validate(fn: FnId, terminal: TerminalId, requireClientSecret: true));
        Assert.NotNull(Validate(fn: FnId, terminal: TerminalId, requireDpop: false));
        Assert.NotNull(Validate(fn: FnId, terminal: TerminalId, accessTokenType: AccessTokenType.Jwt));
        Assert.NotNull(Validate(fn: FnId, terminal: TerminalId, rpId: null));
        Assert.NotNull(Validate(fn: FnId, terminal: TerminalId, rpId: "  "));
    }

    [Theory]
    [InlineData("client_credentials")]
    [InlineData("authorization_code")]
    [InlineData("urn:cocoar:passkey")]
    public void An_extra_grant_breaks_the_exact_grant_set(string extra)
        => Assert.NotNull(Validate(fn: FnId, terminal: TerminalId, grants: [.. ValidGrants, extra]));

    [Fact]
    public void A_missing_grant_breaks_the_exact_grant_set()
        => Assert.NotNull(Validate(fn: FnId, terminal: TerminalId,
            grants: ["urn:ietf:params:oauth:grant-type:device_code", "refresh_token"]));

    [Fact]
    public void The_staffing_grant_is_user_flow_so_an_sa_client_can_never_carry_it()
    {
        // The fail-open trap from the pre-MG-FT-00 feasibility check: without
        // the UserFlowGrantTypes entry this combination would PASS and break
        // the one-auth-mode rule silently.
        var err = OAuthAdminMapping.ValidateServiceAccountLinkInvariant(
            ["client_credentials", PositionGrantTypes.StaffingSession],
            linkedServiceAccountId: Guid.NewGuid());
        Assert.NotNull(err);
        Assert.Equal("OAuth.ServiceAccountLinkRequiresClientCredentialsOnly", err!.Value.Code);
    }

    [Fact]
    public void The_staffing_grant_maps_to_a_gt_permission()
        => Assert.Equal("gt:" + PositionGrantTypes.StaffingSession,
            OAuthAdminMapping.MapGrantTypeToPermission(PositionGrantTypes.StaffingSession));
}
