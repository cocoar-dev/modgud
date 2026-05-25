using Modgud.Domain.OAuth.Apis;
using Modgud.Infrastructure.Persistence.Marten.Projections.OAuth;

namespace Modgud.Tests.Unit.Infrastructure.Persistence.Marten.Projections.OAuth;

/// <summary>
/// Pins the inline projection rules for <see cref="OAuthApiState"/>: every
/// event MUST land on exactly the field the aggregate intends to mutate.
/// Drift here breaks validation in the OAuth admin layer (it reads State
/// synchronously to enforce uniqueness/conflict checks).
/// </summary>
public class OAuthApiStateProjectionTests
{
    private static OAuthApiState NewState() =>
        new OAuthApiStateProjection().Create(new OAuthApiCreated(
            Guid.NewGuid(),
            Name: "billing-api",
            DisplayName: "Billing",
            Description: "Billing endpoints",
            Enabled: true,
            Scopes: new[] { "billing:read" }));

    public class Create
    {
        [Fact]
        public void Initialises_all_fields_from_event()
        {
            var id = Guid.NewGuid();
            var state = new OAuthApiStateProjection().Create(new OAuthApiCreated(
                id, "api", "Api", "Desc", Enabled: true,
                Scopes: new[] { "s1", "s2" }));

            Assert.Equal(id, state.Id);
            Assert.Equal("api", state.Name);
            Assert.Equal("Api", state.DisplayName);
            Assert.Equal("Desc", state.Description);
            Assert.True(state.Enabled);
            Assert.Equal(new[] { "s1", "s2" }, state.Scopes);
            Assert.False(state.IsDeleted);
        }

        [Fact]
        public void Stores_scopes_in_a_mutable_list_copy()
        {
            // Apply* methods later overwrite Scopes via ToList(); the initial
            // list MUST also be a copy so mutating the source array doesn't
            // poison cached projection state.
            var sourceScopes = new[] { "a", "b" };
            var state = new OAuthApiStateProjection().Create(new OAuthApiCreated(
                Guid.NewGuid(), "n", null, null, Enabled: true, Scopes: sourceScopes));

            sourceScopes[0] = "MUTATED";

            Assert.Equal("a", state.Scopes[0]);
        }
    }

    public class Apply
    {
        [Fact]
        public void DisplayName_change_updates_field()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();

            p.Apply(new OAuthApiDisplayNameChanged(s.Id, "New Display"), s);

            Assert.Equal("New Display", s.DisplayName);
        }

        [Fact]
        public void Description_change_supports_null()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();

            p.Apply(new OAuthApiDescriptionChanged(s.Id, null), s);

            Assert.Null(s.Description);
        }

        [Fact]
        public void Enabled_event_sets_true_and_Disabled_sets_false()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();

            p.Apply(new OAuthApiDisabled(s.Id), s);
            Assert.False(s.Enabled);

            p.Apply(new OAuthApiEnabled(s.Id), s);
            Assert.True(s.Enabled);
        }

        [Fact]
        public void Scopes_change_replaces_list()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();

            p.Apply(new OAuthApiScopesChanged(s.Id, new[] { "x", "y", "z" }), s);

            Assert.Equal(new[] { "x", "y", "z" }, s.Scopes);
        }

        [Fact]
        public void UserClaims_change_replaces_list()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();

            p.Apply(new OAuthApiUserClaimsChanged(s.Id, new[] { "name", "email" }), s);

            Assert.Equal(new[] { "name", "email" }, s.UserClaims);
        }

        [Fact]
        public void Properties_change_replaces_dictionary()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();
            s.Properties["legacy"] = "x";

            p.Apply(new OAuthApiPropertiesChanged(s.Id,
                new Dictionary<string, object?> { ["new"] = 42 }), s);

            Assert.False(s.Properties.ContainsKey("legacy"));
            Assert.Equal(42, s.Properties["new"]);
        }

        [Fact]
        public void Deleted_sets_IsDeleted_flag()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();

            p.Apply(new OAuthApiDeleted(s.Id), s);

            Assert.True(s.IsDeleted);
        }

        [Fact]
        public void AppId_change_updates_field()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();
            var appId = Guid.NewGuid();

            p.Apply(new OAuthApiAppIdChanged(s.Id, appId), s);

            Assert.Equal(appId, s.AppId);
        }

        [Fact]
        public void AppId_null_detaches_link()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();
            s.AppId = Guid.NewGuid();

            p.Apply(new OAuthApiAppIdChanged(s.Id, null), s);

            Assert.Null(s.AppId);
        }

        [Fact]
        public void PermissionIds_change_replaces_subset()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

            p.Apply(new OAuthApiPermissionIdsChanged(s.Id, ids), s);

            Assert.Equal(ids, s.PermissionIds);
        }

        [Fact]
        public void PermissionIds_empty_list_clears_subset()
        {
            var p = new OAuthApiStateProjection();
            var s = NewState();
            s.PermissionIds = [Guid.NewGuid(), Guid.NewGuid()];

            p.Apply(new OAuthApiPermissionIdsChanged(s.Id, Array.Empty<Guid>()), s);

            Assert.Empty(s.PermissionIds);
        }
    }

    public class EventReplay
    {
        [Fact]
        public void Full_lifecycle_replays_to_final_state()
        {
            var id = Guid.NewGuid();
            var p = new OAuthApiStateProjection();

            var s = p.Create(new OAuthApiCreated(id, "billing", "Billing", "old desc", true, Array.Empty<string>()));
            p.Apply(new OAuthApiDisplayNameChanged(id, "Billing API"), s);
            p.Apply(new OAuthApiDescriptionChanged(id, "new desc"), s);
            p.Apply(new OAuthApiScopesChanged(id, new[] { "billing:read", "billing:write" }), s);
            p.Apply(new OAuthApiUserClaimsChanged(id, new[] { "sub" }), s);
            p.Apply(new OAuthApiDisabled(id), s);

            Assert.Equal(id, s.Id);
            Assert.Equal("billing", s.Name);
            Assert.Equal("Billing API", s.DisplayName);
            Assert.Equal("new desc", s.Description);
            Assert.False(s.Enabled);
            Assert.Equal(2, s.Scopes.Count);
            Assert.Equal(new[] { "sub" }, s.UserClaims);
            Assert.False(s.IsDeleted);
        }
    }
}
