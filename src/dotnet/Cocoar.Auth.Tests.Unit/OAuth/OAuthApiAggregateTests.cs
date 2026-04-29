using Cocoar.Auth.Domain.OAuth.Apis;

namespace Cocoar.Auth.Tests.Unit.OAuth;

/// <summary>
/// Pins the event-sourcing contract of <see cref="OAuthApiAggregate"/>. The Enable/Disable
/// pair is parameterless (no payload), so we lock down that the resulting events still
/// carry the aggregate id for routing.
/// </summary>
public class OAuthApiAggregateTests
{
    private static (OAuthApiAggregate, OAuthApiCreated) MakeDefault(Guid? id = null) =>
        OAuthApiAggregate.Create(
            id ?? Guid.NewGuid(),
            name: "orders-api",
            displayName: "Orders API",
            description: "Order management",
            enabled: true,
            scopes: new[] { "orders.read", "orders.write" });

    public class Create
    {
        [Fact]
        public void Sets_all_properties_from_event()
        {
            var id = Guid.NewGuid();
            var (agg, evt) = OAuthApiAggregate.Create(
                id, "billing-api", "Billing", "billing description", false,
                new[] { "billing.read" });

            Assert.Equal(id, agg.Id);
            Assert.Equal("billing-api", agg.Name);
            Assert.Equal("Billing", agg.DisplayName);
            Assert.Equal("billing description", agg.Description);
            Assert.False(agg.Enabled);
            Assert.Equal(new[] { "billing.read" }, agg.Scopes);
            Assert.False(agg.IsDeleted);
        }

        [Fact]
        public void Returned_event_id_matches_aggregate_id()
        {
            var id = Guid.NewGuid();
            var (agg, evt) = MakeDefault(id);
            Assert.Equal(id, evt.ApiId);
            Assert.Equal(agg.Id, evt.ApiId);
        }

        [Fact]
        public void Defaults_user_claims_and_properties_to_empty()
        {
            var (agg, _) = MakeDefault();
            Assert.Empty(agg.UserClaims);
            Assert.Empty(agg.Properties);
        }

        [Fact]
        public void Allows_null_optional_strings_and_empty_scopes()
        {
            var (agg, _) = OAuthApiAggregate.Create(
                Guid.NewGuid(), "api", null, null, true, Array.Empty<string>());
            Assert.Null(agg.DisplayName);
            Assert.Null(agg.Description);
            Assert.Empty(agg.Scopes);
        }
    }

    public class Setters
    {
        [Fact]
        public void Each_setter_returns_event_with_aggregate_id_and_mutates_state()
        {
            var (agg, _) = MakeDefault();

            Assert.Equal(agg.Id, agg.SetDisplayName("New").ApiId);
            Assert.Equal("New", agg.DisplayName);

            Assert.Equal(agg.Id, agg.SetDescription("desc2").ApiId);
            Assert.Equal("desc2", agg.Description);

            agg.SetScopes(new[] { "x", "y", "z" });
            Assert.Equal(new[] { "x", "y", "z" }, agg.Scopes);

            agg.SetUserClaims(new[] { "sub", "email" });
            Assert.Equal(new[] { "sub", "email" }, agg.UserClaims);

            agg.SetProperties(new Dictionary<string, object?> { ["k"] = 7 });
            Assert.Equal(7, agg.Properties["k"]);
        }

        [Fact]
        public void Enable_disable_toggles_state_and_returns_id_carrying_event()
        {
            var (agg, _) = MakeDefault();

            var off = agg.Disable();
            Assert.Equal(agg.Id, off.ApiId);
            Assert.False(agg.Enabled);

            var on = agg.Enable();
            Assert.Equal(agg.Id, on.ApiId);
            Assert.True(agg.Enabled);
        }

        [Fact]
        public void Enable_when_already_enabled_is_noop_state_wise()
        {
            var (agg, _) = MakeDefault(); // enabled: true
            agg.Enable();
            Assert.True(agg.Enabled);
        }

        [Fact]
        public void List_and_dict_setters_take_defensive_copies()
        {
            var (agg, _) = MakeDefault();

            var list = new List<string> { "a" };
            agg.SetScopes(list);
            list.Add("b");
            Assert.Single(agg.Scopes);

            var dict = new Dictionary<string, object?> { ["k"] = 1 };
            agg.SetProperties(dict);
            dict["k"] = 99;
            Assert.Equal(1, agg.Properties["k"]);
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
            Assert.Equal(agg.Id, e.ApiId);
        }

        [Fact]
        public void Is_idempotent_when_called_twice()
        {
            var (agg, _) = MakeDefault();
            agg.Delete();
            agg.Delete();
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
            var e2 = original.SetDescription("Desc 2");
            var e3 = original.Disable();
            var e4 = original.SetScopes(new[] { "a", "b" });
            var e5 = original.SetUserClaims(new[] { "c1" });
            var e6 = original.SetProperties(new Dictionary<string, object?> { ["p"] = "v" });
            var e7 = original.Enable();

            var replay = new OAuthApiAggregate();
            replay.Apply(created);
            replay.Apply(e1);
            replay.Apply(e2);
            replay.Apply(e3);
            replay.Apply(e4);
            replay.Apply(e5);
            replay.Apply(e6);
            replay.Apply(e7);

            Assert.Equal(original.Id, replay.Id);
            Assert.Equal(original.Name, replay.Name);
            Assert.Equal(original.DisplayName, replay.DisplayName);
            Assert.Equal(original.Description, replay.Description);
            Assert.Equal(original.Enabled, replay.Enabled);
            Assert.Equal(original.Scopes, replay.Scopes);
            Assert.Equal(original.UserClaims, replay.UserClaims);
            Assert.Equal(original.Properties, replay.Properties);
        }

        [Fact]
        public void Replaying_delete_event_marks_aggregate_deleted()
        {
            var id = Guid.NewGuid();
            var (_, created) = MakeDefault(id);
            var replay = new OAuthApiAggregate();
            replay.Apply(created);
            replay.Apply(new OAuthApiDeleted(id));
            Assert.True(replay.IsDeleted);
        }
    }
}
