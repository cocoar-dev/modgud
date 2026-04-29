using System.Security.Claims;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authentication.Api.Account;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Cocoar.Auth.Authentication.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests.Unit.Authentication.Account;

/// <summary>
/// Pins the pure pieces of <see cref="TwoFactorEnforcementMiddleware"/>:
/// <list type="bullet">
///   <item>The whitelist (<c>IsWhitelisted</c>) — anything callable while grace-locked.</item>
///   <item>The federated-MFA AMR detection (<c>HasFederatedMfa</c>).</item>
///   <item>The early-exit branches of <c>InvokeAsync</c> that never touch Marten or UserManager
///         (level &lt; 1, anonymous, anonymous endpoint, whitelisted path, federated MFA).</item>
/// </list>
/// Branches that hit Marten/UserManager are integration-tested elsewhere — mocking
/// Marten's IDocumentSession + UserManager{T} would dwarf the value here.
/// </summary>
public class TwoFactorEnforcementMiddlewareTests
{
    public class IsWhitelisted
    {
        [Theory]
        [InlineData("/api/account/me")]
        [InlineData("/api/account/logout")]
        [InlineData("/api/account/mfa/setup")]
        [InlineData("/api/account/mfa/enable")]
        [InlineData("/api/account/email-otp/setup")]
        [InlineData("/api/account/passkey/register")]
        [InlineData("/api/account/change-password")]
        [InlineData("/docs/")]
        [InlineData("/docs")]
        [InlineData("/docs/index.html")]
        public void Allows_grace_recovery_and_doc_paths(string path)
        {
            Assert.True(TwoFactorEnforcementMiddleware.IsWhitelisted(path));
        }

        [Theory]
        [InlineData("/api/account/me")]
        [InlineData("/API/ACCOUNT/MFA/setup")]
        [InlineData("/Docs/")]
        public void Match_is_case_insensitive(string path)
        {
            Assert.True(TwoFactorEnforcementMiddleware.IsWhitelisted(path));
        }

        [Theory]
        [InlineData("/api/account/profile")]
        [InlineData("/api/admin/users")]
        [InlineData("/api/auth/login")]
        [InlineData("/")]
        [InlineData("")]
        [InlineData("/document/")]
        public void Rejects_anything_outside_the_allowlist(string path)
        {
            Assert.False(TwoFactorEnforcementMiddleware.IsWhitelisted(path));
        }

        [Fact]
        public void Prefix_match_lets_subpaths_through()
        {
            // "/api/account/mfa/" with the trailing slash means /api/account/mfa
            // (no trailing slash) is NOT a match — pinning the prefix shape so a
            // future "trim trailing slash" tweak surfaces here.
            Assert.True(TwoFactorEnforcementMiddleware.IsWhitelisted("/api/account/mfa/"));
            Assert.True(TwoFactorEnforcementMiddleware.IsWhitelisted("/api/account/mfa/foo"));
            Assert.False(TwoFactorEnforcementMiddleware.IsWhitelisted("/api/account/mfa"));
        }
    }

    public class HasFederatedMfa
    {
        private static ClaimsPrincipal WithExternalAmr(params string[] amrValues)
        {
            var claims = amrValues.Select(v => new Claim("timetodo.external.amr", v)).ToArray();
            var identity = new ClaimsIdentity(claims, authenticationType: "Cookies");
            return new ClaimsPrincipal(identity);
        }

        [Fact]
        public void Null_principal_is_not_federated_mfa()
        {
            Assert.False(TwoFactorEnforcementMiddleware.HasFederatedMfa(null));
        }

        [Fact]
        public void No_amr_claim_is_not_federated_mfa()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity());
            Assert.False(TwoFactorEnforcementMiddleware.HasFederatedMfa(principal));
        }

        [Theory]
        [InlineData("mfa")]
        [InlineData("otp")]
        [InlineData("fido")]
        [InlineData("hwk")]
        [InlineData("swk")]
        [InlineData("mca")]
        [InlineData("pop")]
        public void Recognized_amr_value_is_federated_mfa(string amr)
        {
            Assert.True(TwoFactorEnforcementMiddleware.HasFederatedMfa(WithExternalAmr(amr)));
        }

        [Theory]
        [InlineData("MFA")]
        [InlineData("Otp")]
        [InlineData("Fido")]
        public void Match_is_case_insensitive(string amr)
        {
            Assert.True(TwoFactorEnforcementMiddleware.HasFederatedMfa(WithExternalAmr(amr)));
        }

        [Theory]
        [InlineData("pwd")]
        [InlineData("face")] // not in the accepted set even though arguably MFA
        [InlineData("")]
        [InlineData("magic-link")]
        public void Unrecognized_amr_value_is_not_federated_mfa(string amr)
        {
            Assert.False(TwoFactorEnforcementMiddleware.HasFederatedMfa(WithExternalAmr(amr)));
        }

        [Fact]
        public void Mix_of_recognized_and_unrecognized_amr_values_passes()
        {
            // Multiple amr claims may be present (e.g. pwd + mfa) — any one in the
            // accepted set is enough.
            Assert.True(TwoFactorEnforcementMiddleware.HasFederatedMfa(WithExternalAmr("pwd", "mfa")));
        }

        [Fact]
        public void Standard_amr_claim_name_is_ignored()
        {
            // Only "timetodo.external.amr" is consulted — the bare "amr" claim
            // belongs to the local cookie and is intentionally NOT trusted here
            // (otherwise locally-set claims could spoof federated MFA).
            var identity = new ClaimsIdentity(
                new[] { new Claim("amr", "mfa") }, authenticationType: "Cookies");
            var principal = new ClaimsPrincipal(identity);

            Assert.False(TwoFactorEnforcementMiddleware.HasFederatedMfa(principal));
        }
    }

    public class InvokeAsync_EarlyExits
    {
        private sealed class FakeAuthSettings(int level) : IAuthSettings
        {
            public int AuthenticationMinimumLevel { get; } = level;
            public bool MagicLinkSelfService => false;
            public int TwoFactorGracePeriodDays => 14;
        }

        private static (TwoFactorEnforcementMiddleware mw, Func<bool> wasNextCalled) MakeMiddleware()
        {
            var called = false;
            var mw = new TwoFactorEnforcementMiddleware(_ => { called = true; return Task.CompletedTask; });
            return (mw, () => called);
        }

        [Fact]
        public async Task When_authentication_min_level_is_zero_passes_through_without_touching_dependencies()
        {
            // Because we return early, IDocumentSession and UserManager are never
            // dereferenced — passing `null!` proves the early exit.
            var (mw, wasCalled) = MakeMiddleware();
            var ctx = new DefaultHttpContext();

            await mw.InvokeAsync(ctx, new FakeAuthSettings(level: 0), session: null!, userManager: null!);

            Assert.True(wasCalled());
            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task When_user_is_anonymous_passes_through_without_touching_dependencies()
        {
            var (mw, wasCalled) = MakeMiddleware();
            var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };

            await mw.InvokeAsync(ctx, new FakeAuthSettings(level: 1), session: null!, userManager: null!);

            Assert.True(wasCalled());
        }

        [Fact]
        public async Task When_endpoint_is_marked_anonymous_passes_through_without_touching_dependencies()
        {
            var (mw, wasCalled) = MakeMiddleware();
            var ctx = AuthenticatedContext("/api/admin/users");
            ctx.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask,
                new Microsoft.AspNetCore.Http.EndpointMetadataCollection(new AllowAnonymousAttribute()),
                "anon-endpoint"));

            await mw.InvokeAsync(ctx, new FakeAuthSettings(level: 1), session: null!, userManager: null!);

            Assert.True(wasCalled());
        }

        [Fact]
        public async Task When_path_is_whitelisted_passes_through_without_touching_dependencies()
        {
            var (mw, wasCalled) = MakeMiddleware();
            var ctx = AuthenticatedContext("/api/account/mfa/setup");

            await mw.InvokeAsync(ctx, new FakeAuthSettings(level: 1), session: null!, userManager: null!);

            Assert.True(wasCalled());
        }

        [Fact]
        public async Task When_principal_has_federated_mfa_passes_through_without_touching_dependencies()
        {
            var (mw, wasCalled) = MakeMiddleware();
            var ctx = AuthenticatedContext("/api/admin/users", new Claim("timetodo.external.amr", "mfa"));

            await mw.InvokeAsync(ctx, new FakeAuthSettings(level: 1), session: null!, userManager: null!);

            Assert.True(wasCalled());
        }

        private static DefaultHttpContext AuthenticatedContext(string path, params Claim[] extraClaims)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new(ClaimTypes.Name, "alice"),
            };
            claims.AddRange(extraClaims);
            var identity = new ClaimsIdentity(claims, authenticationType: "Cookies");
            var ctx = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
                RequestServices = new ServiceCollection().BuildServiceProvider(),
            };
            ctx.Request.Path = path;
            return ctx;
        }
    }
}
