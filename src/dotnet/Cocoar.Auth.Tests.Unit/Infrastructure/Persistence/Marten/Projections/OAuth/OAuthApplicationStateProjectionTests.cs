using Cocoar.Auth.Domain.OAuth.Applications;
using Cocoar.Auth.Domain.OAuth.Common;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.OAuth;

namespace Cocoar.Auth.Tests.Unit.Infrastructure.Persistence.Marten.Projections.OAuth;

/// <summary>
/// Pins the inline projection rules for <see cref="OAuthApplicationState"/>,
/// including the SettingsChanged side-effect that parses
/// <see cref="OAuthApplicationSettingKeys.AccessTokenType"/> into the strongly-typed
/// <see cref="AccessTokenType"/> field. That parser is what
/// <c>AccessTokenTypeHandler</c> reads at runtime to switch a client between
/// reference and JWT tokens — silent regressions break SSO clients.
/// </summary>
public class OAuthApplicationStateProjectionTests
{
    private static OAuthApplicationState NewState() =>
        new OAuthApplicationStateProjection().Create(new OAuthApplicationCreated(
            Guid.NewGuid(),
            ClientId: "my-client",
            DisplayName: "My Client",
            ClientType: "confidential",
            ConsentType: "explicit",
            ApplicationType: "web",
            RedirectUris: new[] { "https://app/cb" },
            PostLogoutRedirectUris: new[] { "https://app/" },
            Permissions: new[] { "ept:token" },
            Requirements: new[] { "ft:pkce" }));

    public class Create
    {
        [Fact]
        public void Initialises_all_fields_from_event()
        {
            var id = Guid.NewGuid();
            var s = new OAuthApplicationStateProjection().Create(new OAuthApplicationCreated(
                id, "c", "Display", "public", "implicit", "native",
                new[] { "https://x/cb" },
                new[] { "https://x/" },
                new[] { "ept:token" },
                new[] { "ft:pkce" }));

            Assert.Equal(id, s.Id);
            Assert.Equal("c", s.ClientId);
            Assert.Equal("Display", s.DisplayName);
            Assert.Equal("public", s.ClientType);
            Assert.Equal("implicit", s.ConsentType);
            Assert.Equal("native", s.ApplicationType);
            Assert.Equal(new[] { "https://x/cb" }, s.RedirectUris);
            Assert.Equal(new[] { "https://x/" }, s.PostLogoutRedirectUris);
            Assert.Equal(new[] { "ept:token" }, s.Permissions);
            Assert.Equal(new[] { "ft:pkce" }, s.Requirements);
            Assert.False(s.IsDeleted);
        }

        [Fact]
        public void AccessTokenType_defaults_to_Reference()
        {
            // The default MUST be Reference — that's what the OpenIddict
            // global setting expects, and the JWT switch is opt-in per client.
            var s = NewState();
            Assert.Equal(AccessTokenType.Reference, s.AccessTokenType);
        }
    }

    public class Apply
    {
        [Fact]
        public void DisplayName_change()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            p.Apply(new OAuthApplicationDisplayNameChanged(s.Id, "X"), s);
            Assert.Equal("X", s.DisplayName);
        }

        [Fact]
        public void ClientType_change()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            p.Apply(new OAuthApplicationClientTypeChanged(s.Id, "public"), s);
            Assert.Equal("public", s.ClientType);
        }

        [Fact]
        public void ConsentType_change()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            p.Apply(new OAuthApplicationConsentTypeChanged(s.Id, "implicit"), s);
            Assert.Equal("implicit", s.ConsentType);
        }

        [Fact]
        public void RedirectUris_replaced()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            p.Apply(new OAuthApplicationRedirectUrisChanged(s.Id, new[] { "https://a", "https://b" }), s);
            Assert.Equal(new[] { "https://a", "https://b" }, s.RedirectUris);
        }

        [Fact]
        public void PostLogoutRedirectUris_replaced()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            p.Apply(new OAuthApplicationPostLogoutRedirectUrisChanged(s.Id, new[] { "https://a/" }), s);
            Assert.Equal(new[] { "https://a/" }, s.PostLogoutRedirectUris);
        }

        [Fact]
        public void Permissions_replaced()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            p.Apply(new OAuthApplicationPermissionsChanged(s.Id, new[] { "ept:introspection" }), s);
            Assert.Equal(new[] { "ept:introspection" }, s.Permissions);
        }

        [Fact]
        public void Requirements_replaced()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            p.Apply(new OAuthApplicationRequirementsChanged(s.Id, new[] { "ft:pkce", "ft:dpop" }), s);
            Assert.Equal(new[] { "ft:pkce", "ft:dpop" }, s.Requirements);
        }

        [Fact]
        public void DisplayNames_replaced()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            p.Apply(new OAuthApplicationDisplayNamesChanged(s.Id,
                new Dictionary<string, string> { ["de"] = "Mein Client" }), s);
            Assert.Equal("Mein Client", s.DisplayNames["de"]);
        }

        [Fact]
        public void Properties_replaced()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            p.Apply(new OAuthApplicationPropertiesChanged(s.Id,
                new Dictionary<string, object?> { ["k"] = 1 }), s);
            Assert.Equal(1, s.Properties["k"]);
        }

        [Fact]
        public void Deleted_sets_IsDeleted_flag()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            p.Apply(new OAuthApplicationDeleted(s.Id), s);
            Assert.True(s.IsDeleted);
        }
    }

    public class SettingsChanged_AccessTokenTypeParsing
    {
        [Fact]
        public void Replaces_settings_dictionary()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            s.Settings["legacy"] = "x";

            p.Apply(new OAuthApplicationSettingsChanged(s.Id,
                new Dictionary<string, string> { ["new"] = "y" }), s);

            Assert.False(s.Settings.ContainsKey("legacy"));
            Assert.Equal("y", s.Settings["new"]);
        }

        [Theory]
        [InlineData("Reference", AccessTokenType.Reference)]
        [InlineData("Jwt", AccessTokenType.Jwt)]
        public void Parses_exactly_cased_AccessTokenType_value(string raw, AccessTokenType expected)
        {
            // The projection uses Enum.TryParse without ignoreCase=true, so only
            // the canonical PascalCase enum names parse. See companion test below
            // for the silent fallback when a lowercased value is written.
            var p = new OAuthApplicationStateProjection();
            var s = NewState();

            p.Apply(new OAuthApplicationSettingsChanged(s.Id,
                new Dictionary<string, string> { [OAuthApplicationSettingKeys.AccessTokenType] = raw }), s);

            Assert.Equal(expected, s.AccessTokenType);
        }

        [Theory]
        [InlineData("jwt")]
        [InlineData("JWT")]
        [InlineData("reference")]
        public void FINDING_AccessTokenType_parse_is_case_sensitive_and_silently_ignores_mismatched_casing(string raw)
        {
            // FINDING: Enum.TryParse<AccessTokenType>(v, out var parsed) is the
            // case-sensitive overload. A consumer writing "jwt" instead of "Jwt"
            // sees no parse success → AccessTokenType keeps its previous value,
            // which (for a freshly-created application) means Reference. This is
            // a silent footgun for the Admin API — pinning current behaviour;
            // the fix would be Enum.TryParse(v, ignoreCase: true, ...).
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            // Pre-set to Jwt so we can prove "no change" regardless of intent.
            s.AccessTokenType = AccessTokenType.Jwt;

            p.Apply(new OAuthApplicationSettingsChanged(s.Id,
                new Dictionary<string, string> { [OAuthApplicationSettingKeys.AccessTokenType] = raw }), s);

            Assert.Equal(AccessTokenType.Jwt, s.AccessTokenType);
        }

        [Fact]
        public void Leaves_AccessTokenType_unchanged_when_setting_missing()
        {
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            s.AccessTokenType = AccessTokenType.Jwt;

            p.Apply(new OAuthApplicationSettingsChanged(s.Id,
                new Dictionary<string, string> { ["other"] = "val" }), s);

            Assert.Equal(AccessTokenType.Jwt, s.AccessTokenType);
        }

        [Fact]
        public void Leaves_AccessTokenType_unchanged_when_value_unparseable()
        {
            // Defensive: a typo in the setting MUST NOT silently flip the
            // token strategy. The previous value sticks.
            var p = new OAuthApplicationStateProjection();
            var s = NewState();
            s.AccessTokenType = AccessTokenType.Jwt;

            p.Apply(new OAuthApplicationSettingsChanged(s.Id,
                new Dictionary<string, string> { [OAuthApplicationSettingKeys.AccessTokenType] = "garbage" }), s);

            Assert.Equal(AccessTokenType.Jwt, s.AccessTokenType);
        }
    }
}
