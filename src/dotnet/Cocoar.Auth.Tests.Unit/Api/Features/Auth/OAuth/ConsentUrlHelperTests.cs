using Cocoar.Auth.Api.Features.Auth.OAuth;

namespace Cocoar.Auth.Tests.Unit.Api.Features.Auth.OAuth;

/// <summary>
/// Pinning tests for the URL helpers behind <see cref="ConsentEndpoints"/>.
/// The consent flow is one of the rare places where a user-controlled string
/// (<c>returnUrl</c>) flows from the SPA back into a parser; getting the
/// fallbacks (relative vs. absolute URLs, missing query params, malformed
/// input) wrong silently sends users to a denied page or misses scopes.
/// </summary>
public class ConsentUrlHelperTests
{
    public class ParseAuthorizationUrl
    {
        [Fact]
        public void Extracts_client_id_from_relative_returnUrl()
        {
            var (clientId, _) = ConsentUrlHelper.ParseAuthorizationUrl(
                "/connect/authorize?client_id=spa&scope=openid%20profile");

            Assert.Equal("spa", clientId);
        }

        [Fact]
        public void Extracts_client_id_from_absolute_returnUrl()
        {
            var (clientId, _) = ConsentUrlHelper.ParseAuthorizationUrl(
                "https://auth.example.com/connect/authorize?client_id=web");

            Assert.Equal("web", clientId);
        }

        [Fact]
        public void Splits_space_separated_scopes_into_a_list()
        {
            var (_, scopes) = ConsentUrlHelper.ParseAuthorizationUrl(
                "/connect/authorize?client_id=spa&scope=openid%20profile%20email");

            Assert.Equal(new[] { "openid", "profile", "email" }, scopes);
        }

        [Fact]
        public void Returns_empty_scopes_when_query_has_no_scope_param()
        {
            var (clientId, scopes) = ConsentUrlHelper.ParseAuthorizationUrl(
                "/connect/authorize?client_id=spa");

            Assert.Equal("spa", clientId);
            Assert.Empty(scopes);
        }

        [Fact]
        public void Returns_empty_scopes_when_scope_param_is_blank()
        {
            var (_, scopes) = ConsentUrlHelper.ParseAuthorizationUrl(
                "/connect/authorize?client_id=spa&scope=");

            Assert.Empty(scopes);
        }

        [Fact]
        public void Returns_null_client_id_when_param_missing()
        {
            var (clientId, _) = ConsentUrlHelper.ParseAuthorizationUrl(
                "/connect/authorize?scope=openid");

            Assert.Null(clientId);
        }

        [Fact]
        public void Drops_empty_scope_segments_from_double_spaces()
        {
            // Defensive: a leading/trailing/double space in `scope` should not
            // produce an empty-string scope name. OpenIddict would reject those
            // as unknown scopes much later in the pipeline.
            var (_, scopes) = ConsentUrlHelper.ParseAuthorizationUrl(
                "/connect/authorize?client_id=c&scope=%20openid%20%20profile%20");

            Assert.Equal(new[] { "openid", "profile" }, scopes);
        }

        [Fact]
        public void Returns_null_and_empty_for_unparseable_input()
        {
            // The catch-all in the parser is the only line preventing a hostile
            // returnUrl from raising a 500 — pin it.
            var (clientId, scopes) = ConsentUrlHelper.ParseAuthorizationUrl("::not a url::");

            Assert.Null(clientId);
            Assert.Empty(scopes);
        }

        [Fact]
        public void Picks_first_value_when_client_id_appears_multiple_times()
        {
            var (clientId, _) = ConsentUrlHelper.ParseAuthorizationUrl(
                "/connect/authorize?client_id=first&client_id=second");

            Assert.Equal("first", clientId);
        }
    }

    public class AppendErrorToUrl
    {
        [Fact]
        public void Always_redirects_to_consent_denied_path_regardless_of_input_url()
        {
            // Hard-coded path means a hostile returnUrl can never bounce the user
            // away from /consent/denied — only the OAuth error code/description carry through.
            var redirect = ConsentUrlHelper.AppendErrorToUrl(
                "https://attacker.example.com/anywhere", "access_denied", "denied");

            Assert.StartsWith("/consent/denied", redirect);
        }

        [Fact]
        public void Url_encodes_error_and_description_query_values()
        {
            var redirect = ConsentUrlHelper.AppendErrorToUrl(
                url: "/connect/authorize", error: "access_denied",
                description: "user said no");

            Assert.Contains("error=access_denied", redirect);
            Assert.Contains("error_description=user%20said%20no", redirect);
        }

        [Fact]
        public void Encodes_special_characters_safely()
        {
            var redirect = ConsentUrlHelper.AppendErrorToUrl(
                "/", "weird&error", "<script>alert(1)</script>");

            Assert.DoesNotContain("<script>", redirect);
            Assert.Contains("error=weird%26error", redirect);
        }
    }
}
