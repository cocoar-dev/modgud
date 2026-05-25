using Modgud.Domain.OAuth.Scopes;

namespace Modgud.Tests.Unit.OAuth;

/// <summary>
/// Pins the event-sourcing contract of <see cref="OAuthScopeAggregate"/>.
/// Scope events carry no PII, so all state must round-trip through the stream.
/// </summary>
public class OAuthScopeAggregateTests
{
    private static (OAuthScopeAggregate, OAuthScopeCreated) MakeDefault(Guid? id = null) =>
        OAuthScopeAggregate.Create(
            id ?? Guid.NewGuid(),
            name: "api.read",
            displayName: "Read API",
            description: "Read access to the API",
            resources: new[] { "api1" });

    public class Create
    {
        [Fact]
        public void Sets_all_properties_from_event()
        {
            var id = Guid.NewGuid();
            var (agg, evt) = OAuthScopeAggregate.Create(id, "api.read", "Read", "desc", new[] { "api1", "api2" });

            Assert.Equal(id, agg.Id);
            Assert.Equal("api.read", agg.Name);
            Assert.Equal("Read", agg.DisplayName);
            Assert.Equal("desc", agg.Description);
            Assert.Equal(new[] { "api1", "api2" }, agg.Resources);
            Assert.False(agg.IsDeleted);
        }

        [Fact]
        public void Returned_event_id_matches_aggregate_id()
        {
            var id = Guid.NewGuid();
            var (agg, evt) = MakeDefault(id);
            Assert.Equal(id, evt.ScopeId);
            Assert.Equal(agg.Id, evt.ScopeId);
        }

        [Fact]
        public void Defaults_enabled_true_required_false_emphasize_false_show_in_discovery_true()
        {
            var (agg, _) = MakeDefault();
            Assert.True(agg.Enabled);
            Assert.False(agg.Required);
            Assert.False(agg.Emphasize);
            Assert.True(agg.ShowInDiscoveryDocument);
        }

        [Fact]
        public void Defaults_collections_to_empty()
        {
            var (agg, _) = MakeDefault();
            Assert.Empty(agg.DisplayNames);
            Assert.Empty(agg.Descriptions);
            Assert.Empty(agg.Properties);
            Assert.Empty(agg.UserClaims);
        }
    }

    public class Setters
    {
        [Fact]
        public void Each_setter_returns_event_with_aggregate_id_and_mutates_state()
        {
            var (agg, _) = MakeDefault();

            Assert.Equal(agg.Id, agg.SetDisplayName("New").ScopeId);
            Assert.Equal("New", agg.DisplayName);

            Assert.Equal(agg.Id, agg.SetDescription("desc2").ScopeId);
            Assert.Equal("desc2", agg.Description);

            agg.SetResources(new[] { "api2" });
            Assert.Equal(new[] { "api2" }, agg.Resources);

            agg.SetDisplayNames(new Dictionary<string, string> { ["de"] = "Lesen" });
            Assert.Equal("Lesen", agg.DisplayNames["de"]);

            agg.SetDescriptions(new Dictionary<string, string> { ["de"] = "Lesezugriff" });
            Assert.Equal("Lesezugriff", agg.Descriptions["de"]);

            agg.SetProperties(new Dictionary<string, object?> { ["k"] = 1 });
            Assert.Equal(1, agg.Properties["k"]);

            agg.SetUserClaims(new[] { "email", "name" });
            Assert.Equal(new[] { "email", "name" }, agg.UserClaims);
        }

        [Fact]
        public void Boolean_setters_toggle_state()
        {
            var (agg, _) = MakeDefault();

            agg.SetEnabled(false);
            Assert.False(agg.Enabled);
            agg.SetEnabled(true);
            Assert.True(agg.Enabled);

            agg.SetRequired(true);
            Assert.True(agg.Required);

            agg.SetEmphasize(true);
            Assert.True(agg.Emphasize);

            agg.SetShowInDiscoveryDocument(false);
            Assert.False(agg.ShowInDiscoveryDocument);
        }

        [Fact]
        public void List_and_dict_setters_take_defensive_copies()
        {
            var (agg, _) = MakeDefault();

            var list = new List<string> { "api1" };
            agg.SetResources(list);
            list.Add("api2");
            Assert.Single(agg.Resources);

            var dict = new Dictionary<string, string> { ["en"] = "Read" };
            agg.SetDisplayNames(dict);
            dict["de"] = "Lesen";
            Assert.Single(agg.DisplayNames);
        }

        [Fact]
        public void Allows_null_for_optional_strings()
        {
            var (agg, _) = MakeDefault();
            agg.SetDisplayName(null);
            agg.SetDescription(null);
            Assert.Null(agg.DisplayName);
            Assert.Null(agg.Description);
        }
    }

    public class Delete
    {
        [Fact]
        public void Marks_aggregate_as_deleted_and_returns_event()
        {
            var (agg, _) = MakeDefault();
            var e = agg.Delete();
            Assert.True(agg.IsDeleted);
            Assert.Equal(agg.Id, e.ScopeId);
        }

        [Fact]
        public void Is_idempotent_when_called_twice()
        {
            var (agg, _) = MakeDefault();
            agg.Delete();
            agg.Delete();
            Assert.True(agg.IsDeleted);
        }

        [Fact]
        public void Setters_after_delete_still_apply_aggregate_does_not_self_guard()
        {
            var (agg, _) = MakeDefault();
            agg.Delete();
            agg.SetDisplayName("after-delete");
            Assert.Equal("after-delete", agg.DisplayName);
            Assert.True(agg.IsDeleted);
        }
    }

    public class Replay
    {
        [Fact]
        public void Replaying_full_event_stream_on_fresh_instance_reproduces_state()
        {
            var id = Guid.NewGuid();
            var (original, created) = MakeDefault(id);

            var e1 = original.SetDisplayName("Display 2");
            var e2 = original.SetResources(new[] { "api1", "api2" });
            var e3 = original.SetEnabled(false);
            var e4 = original.SetRequired(true);
            var e5 = original.SetUserClaims(new[] { "email" });
            var e6 = original.SetProperties(new Dictionary<string, object?> { ["x"] = "y" });

            var replay = new OAuthScopeAggregate();
            replay.Apply(created);
            replay.Apply(e1);
            replay.Apply(e2);
            replay.Apply(e3);
            replay.Apply(e4);
            replay.Apply(e5);
            replay.Apply(e6);

            Assert.Equal(original.Id, replay.Id);
            Assert.Equal(original.Name, replay.Name);
            Assert.Equal(original.DisplayName, replay.DisplayName);
            Assert.Equal(original.Resources, replay.Resources);
            Assert.Equal(original.Enabled, replay.Enabled);
            Assert.Equal(original.Required, replay.Required);
            Assert.Equal(original.UserClaims, replay.UserClaims);
            Assert.Equal(original.Properties, replay.Properties);
        }
    }
}
