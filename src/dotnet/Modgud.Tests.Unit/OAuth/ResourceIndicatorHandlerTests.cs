using System.Security.Claims;
using Modgud.Infrastructure.OpenIddict;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Tests.Unit.OAuth;

/// <summary>
/// Pins the RFC 8707 (Resource Indicators) behaviour. The handler is the
/// piece that makes a token issued for cocoar-policy unable to replay
/// against any other resource sharing the IdP — without this, the
/// MCP-spec compliance story falls apart silently.
///
/// <para>Three scenarios:</para>
/// <list type="bullet">
///   <item><description>No <c>resource</c> param → handler is a no-op,
///   audience stays as the upstream-set scope-bound list.</description></item>
///   <item><description><c>resource</c> param matches a granted resource →
///   audience narrows to exactly the requested set.</description></item>
///   <item><description><c>resource</c> param NOT in granted resources →
///   sign-in is rejected with <c>invalid_target</c>; no token is issued.</description></item>
/// </list>
/// </summary>
public class ResourceIndicatorHandlerTests
{
    [Fact]
    public async Task NoResourceParam_PreservesAudience()
    {
        var handler = new ResourceIndicatorHandler();
        var ctx = MakeContext(
            grantedResources: ["https://api.example.com/", "https://other.example.com/"],
            requestedResources: []);

        await handler.HandleAsync(ctx);

        Assert.False(ctx.IsRejected);
        var resources = ctx.Principal!.GetResources().ToList();
        Assert.Equal(2, resources.Count);
        Assert.Contains("https://api.example.com/", resources);
        Assert.Contains("https://other.example.com/", resources);
    }

    [Fact]
    public async Task RequestedResource_AuthorisedByScopes_NarrowsAudience()
    {
        var handler = new ResourceIndicatorHandler();
        var ctx = MakeContext(
            grantedResources: ["https://api.example.com/", "https://other.example.com/", "client-id-fallback"],
            requestedResources: ["https://api.example.com/"]);

        await handler.HandleAsync(ctx);

        Assert.False(ctx.IsRejected);
        var resources = ctx.Principal!.GetResources().ToList();
        Assert.Single(resources);
        Assert.Equal("https://api.example.com/", resources[0]);
    }

    [Fact]
    public async Task RequestedResource_NotAuthorised_RejectsWithInvalidTarget()
    {
        var handler = new ResourceIndicatorHandler();
        var ctx = MakeContext(
            grantedResources: ["https://api.example.com/"],
            requestedResources: ["https://attacker.example/"]);

        await handler.HandleAsync(ctx);

        Assert.True(ctx.IsRejected);
        Assert.Equal(Errors.InvalidTarget, ctx.Error);
        Assert.Contains("https://attacker.example/", ctx.ErrorDescription);
    }

    [Fact]
    public async Task RequestedMultipleResources_AllAuthorised_NarrowsToAll()
    {
        var handler = new ResourceIndicatorHandler();
        var ctx = MakeContext(
            grantedResources: ["https://a.example/", "https://b.example/", "https://c.example/"],
            requestedResources: ["https://a.example/", "https://b.example/"]);

        await handler.HandleAsync(ctx);

        Assert.False(ctx.IsRejected);
        var resources = ctx.Principal!.GetResources().ToList();
        Assert.Equal(2, resources.Count);
        Assert.Contains("https://a.example/", resources);
        Assert.Contains("https://b.example/", resources);
        Assert.DoesNotContain("https://c.example/", resources);
    }

    [Fact]
    public async Task PartiallyInvalidRequest_RejectsWholeRequest()
    {
        // RFC 8707 §2.2 — if any requested resource is not authorised,
        // the whole request must be rejected. We must not silently drop
        // the bad ones and continue with the good ones.
        var handler = new ResourceIndicatorHandler();
        var ctx = MakeContext(
            grantedResources: ["https://good.example/"],
            requestedResources: ["https://good.example/", "https://bad.example/"]);

        await handler.HandleAsync(ctx);

        Assert.True(ctx.IsRejected);
        Assert.Equal(Errors.InvalidTarget, ctx.Error);
    }

    private static OpenIddictServerEvents.ProcessSignInContext MakeContext(
        string[] grantedResources, string[] requestedResources)
    {
        var transaction = new OpenIddictServerTransaction
        {
            Request = new OpenIddictRequest(),
            Options = new OpenIddictServerOptions
            {
                JsonWebTokenHandler = new(),
                SigningCredentials =
                {
                    new SigningCredentials(
                        new SymmetricSecurityKey(new byte[32]),
                        SecurityAlgorithms.HmacSha256)
                },
            },
        };

        if (requestedResources.Length > 0)
        {
            transaction.Request.Resources = System.Collections.Immutable.ImmutableArray.Create<string?>(requestedResources);
        }

        var identity = new ClaimsIdentity("Bearer");
        identity.SetClaim(Claims.Subject, "test-user");
        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(grantedResources);

        var context = new OpenIddictServerEvents.ProcessSignInContext(transaction)
        {
            Principal = principal,
        };

        return context;
    }
}
