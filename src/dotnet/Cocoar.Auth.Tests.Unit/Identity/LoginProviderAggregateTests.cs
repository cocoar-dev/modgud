using Cocoar.Auth.Domain.Identity.LoginProviders;

namespace Cocoar.Auth.Tests.Unit.Identity;

/// <summary>
/// Pins the event-sourcing contract of <see cref="LoginProviderAggregate"/>.
/// Configuration is a <see cref="Dictionary{TKey,TValue}"/> (concrete type, not
/// IReadOnlyDictionary) — the aggregate must still defensively copy it.
/// </summary>
public class LoginProviderAggregateTests
{
    private static (LoginProviderAggregate, LoginProviderCreated) MakeDefault(
        Guid? id = null, LoginProviderType type = LoginProviderType.Internal, bool isBuiltIn = false) =>
        LoginProviderAggregate.Create(
            id ?? Guid.NewGuid(),
            name: "internal",
            displayName: "Internal",
            description: "Built-in password auth",
            type: type,
            configuration: new Dictionary<string, string> { ["k"] = "v" },
            isBuiltIn: isBuiltIn);

    public class Create
    {
        [Fact]
        public void Sets_all_properties_from_event()
        {
            var id = Guid.NewGuid();
            var (agg, evt) = LoginProviderAggregate.Create(
                id, "google", "Google", "Sign in with Google",
                LoginProviderType.OpenIdConnect,
                new Dictionary<string, string> { ["client_id"] = "abc" },
                isBuiltIn: false);

            Assert.Equal(id, agg.Id);
            Assert.Equal("google", agg.Name);
            Assert.Equal("Google", agg.DisplayName);
            Assert.Equal("Sign in with Google", agg.Description);
            Assert.Equal(LoginProviderType.OpenIdConnect, agg.Type);
            Assert.Equal("abc", agg.Configuration["client_id"]);
            Assert.False(agg.IsBuiltIn);
            Assert.False(agg.IsDeleted);
        }

        [Fact]
        public void Returned_event_id_matches_aggregate_id()
        {
            var id = Guid.NewGuid();
            var (agg, evt) = MakeDefault(id);
            Assert.Equal(id, evt.LoginProviderId);
            Assert.Equal(agg.Id, evt.LoginProviderId);
        }

        [Fact]
        public void Built_in_flag_is_preserved()
        {
            var (agg, _) = MakeDefault(isBuiltIn: true);
            Assert.True(agg.IsBuiltIn);
        }

        [Fact]
        public void Allows_null_optional_strings_and_empty_configuration()
        {
            var (agg, _) = LoginProviderAggregate.Create(
                Guid.NewGuid(), "p", null, null, LoginProviderType.Internal,
                new Dictionary<string, string>(), isBuiltIn: false);

            Assert.Null(agg.DisplayName);
            Assert.Null(agg.Description);
            Assert.Empty(agg.Configuration);
        }

        [Fact]
        public void Configuration_is_defensively_copied_from_create_event()
        {
            var cfg = new Dictionary<string, string> { ["a"] = "1" };
            var (agg, _) = LoginProviderAggregate.Create(
                Guid.NewGuid(), "p", null, null, LoginProviderType.Internal, cfg, false);

            cfg["b"] = "2";

            Assert.Single(agg.Configuration);
            Assert.False(agg.Configuration.ContainsKey("b"));
        }
    }

    public class Setters
    {
        [Fact]
        public void Each_setter_returns_event_with_aggregate_id_and_mutates_state()
        {
            var (agg, _) = MakeDefault();

            var n = agg.SetName("renamed");
            Assert.Equal(agg.Id, n.LoginProviderId);
            Assert.Equal("renamed", agg.Name);

            var dn = agg.SetDisplayName("New DN");
            Assert.Equal(agg.Id, dn.LoginProviderId);
            Assert.Equal("New DN", agg.DisplayName);

            var d = agg.SetDescription("New Desc");
            Assert.Equal(agg.Id, d.LoginProviderId);
            Assert.Equal("New Desc", agg.Description);

            var cfg = agg.SetConfiguration(new Dictionary<string, string> { ["x"] = "y" });
            Assert.Equal(agg.Id, cfg.LoginProviderId);
            Assert.Equal("y", agg.Configuration["x"]);
        }

        [Fact]
        public void SetConfiguration_replaces_existing_configuration_completely()
        {
            var (agg, _) = MakeDefault(); // has { k = v }
            agg.SetConfiguration(new Dictionary<string, string> { ["new"] = "1" });
            Assert.False(agg.Configuration.ContainsKey("k"));
            Assert.Equal("1", agg.Configuration["new"]);
        }

        [Fact]
        public void SetConfiguration_takes_defensive_copy()
        {
            var (agg, _) = MakeDefault();
            var input = new Dictionary<string, string> { ["a"] = "1" };
            agg.SetConfiguration(input);
            input["b"] = "2";
            Assert.Single(agg.Configuration);
            Assert.False(agg.Configuration.ContainsKey("b"));
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
            Assert.Equal(agg.Id, e.LoginProviderId);
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
            // Built-in providers are protected at application layer, not here.
            var (agg, _) = MakeDefault(isBuiltIn: true);
            agg.Delete();
            agg.SetDisplayName("after");
            Assert.Equal("after", agg.DisplayName);
            Assert.True(agg.IsDeleted);
        }
    }

    public class Replay
    {
        [Fact]
        public void Replaying_full_event_stream_on_fresh_instance_reproduces_state()
        {
            var id = Guid.NewGuid();
            var (original, created) = LoginProviderAggregate.Create(
                id, "p", "P", "desc", LoginProviderType.OpenIdConnect,
                new Dictionary<string, string> { ["a"] = "1" }, isBuiltIn: false);

            var e1 = original.SetName("renamed");
            var e2 = original.SetDisplayName("Renamed");
            var e3 = original.SetDescription("New desc");
            var e4 = original.SetConfiguration(new Dictionary<string, string> { ["b"] = "2" });

            var replay = new LoginProviderAggregate();
            replay.Apply(created);
            replay.Apply(e1);
            replay.Apply(e2);
            replay.Apply(e3);
            replay.Apply(e4);

            Assert.Equal(original.Id, replay.Id);
            Assert.Equal(original.Name, replay.Name);
            Assert.Equal(original.DisplayName, replay.DisplayName);
            Assert.Equal(original.Description, replay.Description);
            Assert.Equal(original.Type, replay.Type);
            Assert.Equal(original.Configuration, replay.Configuration);
            Assert.Equal(original.IsBuiltIn, replay.IsBuiltIn);
        }

        [Fact]
        public void Replaying_delete_event_marks_aggregate_deleted()
        {
            var id = Guid.NewGuid();
            var (_, created) = MakeDefault(id);
            var replay = new LoginProviderAggregate();
            replay.Apply(created);
            replay.Apply(new LoginProviderDeleted(id));
            Assert.True(replay.IsDeleted);
        }
    }
}
