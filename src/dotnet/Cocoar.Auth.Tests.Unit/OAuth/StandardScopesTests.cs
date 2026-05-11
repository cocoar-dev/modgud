using Cocoar.Auth.Domain.OAuth.Scopes;

namespace Cocoar.Auth.Tests.Unit.OAuth;

/// <summary>
/// Pins the OIDC standard-scope set. The admin UI hides delete on these so they
/// can never be removed from a realm — drift here would leak that protection.
/// </summary>
public class StandardScopesTests
{
    public class IsStandard
    {
        [Theory]
        [InlineData("openid")]
        [InlineData("email")]
        [InlineData("profile")]
        [InlineData("phone")]
        [InlineData("address")]
        [InlineData("roles")]
        [InlineData("offline_access")]
        [InlineData("permissions")]
        public void Recognises_each_standard_scope(string scope) =>
            Assert.True(StandardScopes.IsStandard(scope));

        [Theory]
        [InlineData("custom")]
        [InlineData("api.read")]
        [InlineData("")]
        public void Rejects_non_standard_scopes(string scope) =>
            Assert.False(StandardScopes.IsStandard(scope));

        [Fact]
        public void Null_is_not_standard() =>
            Assert.False(StandardScopes.IsStandard(null));

        [Fact]
        public void Match_is_case_sensitive()
        {
            // OIDC scope names are lowercase by spec — matching them in a case-insensitive
            // way would silently allow "OpenID" to be treated as a standard scope.
            Assert.False(StandardScopes.IsStandard("OpenId"));
            Assert.False(StandardScopes.IsStandard("EMAIL"));
        }
    }

    public class AllSet
    {
        [Fact]
        public void Contains_eight_scopes()
        {
            // OIDC core (openid + email + profile + phone + address + offline_access)
            // plus the Cocoar-specific authz-claim gates (roles + permissions).
            Assert.Equal(8, StandardScopes.All.Count);
        }

        [Fact]
        public void Contains_no_duplicates()
        {
            Assert.Equal(StandardScopes.All.Count, StandardScopes.All.Distinct().Count());
        }

        [Fact]
        public void Every_member_is_recognised_by_IsStandard()
        {
            foreach (var scope in StandardScopes.All)
                Assert.True(StandardScopes.IsStandard(scope), $"All-set member '{scope}' is not recognised by IsStandard.");
        }
    }
}
