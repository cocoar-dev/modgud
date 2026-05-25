using Modgud.Domain.OAuth.Scopes;
using Modgud.Infrastructure.Persistence.Marten.Projections.OAuth;

namespace Modgud.Tests.Unit.Infrastructure.Persistence.Marten.Projections.OAuth;

/// <summary>
/// Pins the inline projection rules for <see cref="OAuthScopeState"/>. Scope
/// state drives both the OpenIddict scope store and admin uniqueness checks;
/// any silent drift between aggregate and projection breaks both.
/// </summary>
public class OAuthScopeStateProjectionTests
{
    private static OAuthScopeState NewState() =>
        new OAuthScopeStateProjection().Create(new OAuthScopeCreated(
            Guid.NewGuid(),
            Name: "billing.read",
            DisplayName: "Read billing",
            Description: "Read access to billing",
            Resources: new[] { "billing-api" }));

    public class Create
    {
        [Fact]
        public void Initialises_all_fields_from_event()
        {
            var id = Guid.NewGuid();
            var s = new OAuthScopeStateProjection().Create(new OAuthScopeCreated(
                id, "scope.x", "Scope X", "desc", new[] { "r1" }));

            Assert.Equal(id, s.Id);
            Assert.Equal("scope.x", s.Name);
            Assert.Equal("Scope X", s.DisplayName);
            Assert.Equal("desc", s.Description);
            Assert.Equal(new[] { "r1" }, s.Resources);
            Assert.False(s.IsDeleted);
        }

        [Fact]
        public void Defaults_for_unset_fields_match_state_defaults()
        {
            // Defaults coming from OAuthScopeState (the document type) — pinning
            // them ensures Create doesn't accidentally override what the state
            // class promises (Enabled=true, Required=false, etc.).
            var s = new OAuthScopeStateProjection().Create(new OAuthScopeCreated(
                Guid.NewGuid(), "n", null, null, Array.Empty<string>()));

            Assert.True(s.Enabled);
            Assert.False(s.Required);
            Assert.False(s.Emphasize);
            Assert.True(s.ShowInDiscoveryDocument);
            Assert.Empty(s.UserClaims);
            Assert.Empty(s.DisplayNames);
            Assert.Empty(s.Descriptions);
            Assert.Empty(s.Properties);
        }
    }

    public class Apply
    {
        [Fact]
        public void DisplayName_change_updates_field()
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeDisplayNameChanged(s.Id, "New"), s);
            Assert.Equal("New", s.DisplayName);
        }

        [Fact]
        public void Description_change_supports_null()
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeDescriptionChanged(s.Id, null), s);
            Assert.Null(s.Description);
        }

        [Fact]
        public void Resources_change_replaces_list()
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeResourcesChanged(s.Id, new[] { "a", "b" }), s);
            Assert.Equal(new[] { "a", "b" }, s.Resources);
        }

        [Fact]
        public void DisplayNames_change_replaces_dictionary()
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeDisplayNamesChanged(s.Id,
                new Dictionary<string, string> { ["de"] = "Lesen" }), s);
            Assert.Single(s.DisplayNames);
            Assert.Equal("Lesen", s.DisplayNames["de"]);
        }

        [Fact]
        public void Descriptions_change_replaces_dictionary()
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeDescriptionsChanged(s.Id,
                new Dictionary<string, string> { ["de"] = "Lesezugriff" }), s);
            Assert.Equal("Lesezugriff", s.Descriptions["de"]);
        }

        [Fact]
        public void Properties_change_replaces_dictionary()
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopePropertiesChanged(s.Id,
                new Dictionary<string, object?> { ["k"] = "v" }), s);
            Assert.Equal("v", s.Properties["k"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Enabled_change_writes_value(bool value)
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeEnabledChanged(s.Id, value), s);
            Assert.Equal(value, s.Enabled);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Required_change_writes_value(bool value)
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeRequiredChanged(s.Id, value), s);
            Assert.Equal(value, s.Required);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Emphasize_change_writes_value(bool value)
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeEmphasizeChanged(s.Id, value), s);
            Assert.Equal(value, s.Emphasize);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ShowInDiscoveryDocument_change_writes_value(bool value)
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeShowInDiscoveryDocumentChanged(s.Id, value), s);
            Assert.Equal(value, s.ShowInDiscoveryDocument);
        }

        [Fact]
        public void UserClaims_change_replaces_list()
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeUserClaimsChanged(s.Id, new[] { "name", "email" }), s);
            Assert.Equal(new[] { "name", "email" }, s.UserClaims);
        }

        [Fact]
        public void Deleted_sets_IsDeleted_flag()
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeDeleted(s.Id), s);
            Assert.True(s.IsDeleted);
        }

        [Fact]
        public void AppIdChanged_assigns_app()
        {
            // Stufe-3 scope restriction relies on AppId — silent regressions
            // here would let app-scoped scopes leak across tenants.
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            var appId = Guid.NewGuid();

            p.Apply(new OAuthScopeAppIdChanged(s.Id, appId), s);

            Assert.Equal(appId, s.AppId);
        }

        [Fact]
        public void AppIdChanged_to_null_makes_scope_global()
        {
            var p = new OAuthScopeStateProjection();
            var s = NewState();
            p.Apply(new OAuthScopeAppIdChanged(s.Id, Guid.NewGuid()), s);
            Assert.NotNull(s.AppId);

            p.Apply(new OAuthScopeAppIdChanged(s.Id, null), s);

            Assert.Null(s.AppId);
        }

        [Fact]
        public void Created_event_initial_state_has_null_AppId()
        {
            // Standard OIDC scopes (openid/email/profile/roles/offline_access)
            // are created without an AppId and stay global by default.
            var s = NewState();
            Assert.Null(s.AppId);
        }
    }
}
