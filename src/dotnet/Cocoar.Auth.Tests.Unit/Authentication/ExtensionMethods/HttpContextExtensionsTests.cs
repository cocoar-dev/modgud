using System.Security.Claims;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Microsoft.AspNetCore.Http;

namespace Cocoar.Auth.Tests.Unit.Authentication.ExtensionMethods;

/// <summary>
/// Pins <see cref="HttpContextExtensions.GetUserId"/>. The handler chain uses this
/// to load <c>UserManager.GetUserAsync</c>; a regression that returns <c>null</c>
/// instead of the parsed Guid would break every authenticated endpoint quietly.
/// </summary>
public class HttpContextExtensionsTests
{
    private static HttpContext WithClaims(params Claim[] claims)
    {
        var ctx = new DefaultHttpContext();
        var identity = new ClaimsIdentity(claims, authenticationType: "Cookies");
        ctx.User = new ClaimsPrincipal(identity);
        return ctx;
    }

    [Fact]
    public void Returns_parsed_guid_when_name_identifier_claim_is_a_valid_guid()
    {
        var id = Guid.NewGuid();
        var ctx = WithClaims(new Claim(ClaimTypes.NameIdentifier, id.ToString()));

        Assert.Equal(id, ctx.GetUserId());
    }

    [Fact]
    public void Returns_null_when_no_name_identifier_claim_is_present()
    {
        var ctx = WithClaims(new Claim(ClaimTypes.Email, "alice@example.com"));

        Assert.Null(ctx.GetUserId());
    }

    [Fact]
    public void Returns_null_when_user_principal_is_anonymous()
    {
        var ctx = new DefaultHttpContext();

        Assert.Null(ctx.GetUserId());
    }

    [Fact]
    public void Returns_null_when_name_identifier_claim_is_not_a_guid()
    {
        var ctx = WithClaims(new Claim(ClaimTypes.NameIdentifier, "not-a-guid"));

        Assert.Null(ctx.GetUserId());
    }

    [Fact]
    public void Returns_null_when_name_identifier_claim_is_empty()
    {
        var ctx = WithClaims(new Claim(ClaimTypes.NameIdentifier, ""));

        Assert.Null(ctx.GetUserId());
    }

    [Fact]
    public void Returns_first_name_identifier_claim_when_duplicates_are_present()
    {
        // FindFirst returns the first match — pin so a future enumerator switch is loud.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var ctx = WithClaims(
            new Claim(ClaimTypes.NameIdentifier, first.ToString()),
            new Claim(ClaimTypes.NameIdentifier, second.ToString()));

        Assert.Equal(first, ctx.GetUserId());
    }
}
