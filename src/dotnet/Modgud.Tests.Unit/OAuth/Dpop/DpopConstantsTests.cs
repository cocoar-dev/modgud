using Modgud.Infrastructure.OpenIddict.Dpop;

namespace Modgud.Tests.Unit.OAuth.Dpop;

/// <summary>
/// Pins the DPoP wire constants (RFC 9449). These cross the wire or key persisted
/// state, so a drift would silently break the binding — a client's <c>cnf.jkt</c>
/// no longer matching, or a DPoP token being announced as <c>Bearer</c>.
/// </summary>
public class DpopConstantsTests
{
    [Fact]
    public void Header_name_is_pinned() => Assert.Equal("DPoP", DpopConstants.HeaderName);

    [Fact]
    public void Token_type_is_pinned() => Assert.Equal("DPoP", DpopConstants.TokenType);

    [Fact]
    public void Confirmation_claim_is_cnf() => Assert.Equal("cnf", DpopConstants.ConfirmationClaim);

    [Fact]
    public void Thumbprint_member_is_jkt() => Assert.Equal("jkt", DpopConstants.JwkThumbprintMember);

    [Fact]
    public void Invalid_proof_error_is_pinned() =>
        Assert.Equal("invalid_dpop_proof", DpopConstants.InvalidProofError);

    [Fact]
    public void Json_claim_value_type_is_pinned() =>
        Assert.Equal("JSON", DpopConstants.JsonClaimValueType);
}
