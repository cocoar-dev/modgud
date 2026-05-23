using Cocoar.Auth.Domain.OAuth.Applications;

namespace Cocoar.Auth.Tests.Unit.OAuth;

/// <summary>
/// Pins the event-sourcing contract of <see cref="OAuthApplicationAggregate"/>:
/// every state mutation MUST go through an event, and replaying the event stream
/// on a fresh instance MUST reproduce the exact state. Drift here breaks projections
/// and rebuilds.
/// </summary>
public class OAuthApplicationAggregateTests
{
    private static (OAuthApplicationAggregate, OAuthApplicationCreated) MakeDefault(Guid? id = null) =>
        OAuthApplicationAggregate.Create(
            id ?? Guid.NewGuid(),
            clientId: "my-client",
            displayName: "My Client",
            clientType: "confidential",
            consentType: "explicit",
            applicationType: "web",
            redirectUris: new[] { "https://app.example/callback" },
            postLogoutRedirectUris: new[] { "https://app.example/" },
            permissions: new[] { "ept:token", "gt:authorization_code" },
            requirements: new[] { "ft:pkce" });

    public class Create
    {
        [Fact]
        public void Sets_all_properties_from_event()
        {
            var id = Guid.NewGuid();
            var (agg, evt) = OAuthApplicationAggregate.Create(
                id, "client-x", "Client X", "public", "implicit", "native",
                new[] { "https://x/cb" },
                new[] { "https://x/" },
                new[] { "ept:token" },
                new[] { "ft:pkce" });

            Assert.Equal(id, agg.Id);
            Assert.Equal("client-x", agg.ClientId);
            Assert.Equal("Client X", agg.DisplayName);
            Assert.Equal("public", agg.ClientType);
            Assert.Equal("implicit", agg.ConsentType);
            Assert.Equal("native", agg.ApplicationType);
            Assert.Equal(new[] { "https://x/cb" }, agg.RedirectUris);
            Assert.Equal(new[] { "https://x/" }, agg.PostLogoutRedirectUris);
            Assert.Equal(new[] { "ept:token" }, agg.Permissions);
            Assert.Equal(new[] { "ft:pkce" }, agg.Requirements);
            Assert.False(agg.IsDeleted);
        }

        [Fact]
        public void Returned_event_id_matches_aggregate_id()
        {
            var id = Guid.NewGuid();
            var (agg, evt) = MakeDefault(id);
            Assert.Equal(id, evt.ApplicationId);
            Assert.Equal(agg.Id, evt.ApplicationId);
        }

        [Fact]
        public void Defaults_settings_displaynames_properties_to_empty()
        {
            var (agg, _) = MakeDefault();
            Assert.Empty(agg.Settings);
            Assert.Empty(agg.DisplayNames);
            Assert.Empty(agg.Properties);
        }

        [Fact]
        public void Allows_null_optional_strings()
        {
            var (agg, _) = OAuthApplicationAggregate.Create(
                Guid.NewGuid(), "c", null, null, null, null,
                Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>());

            Assert.Null(agg.DisplayName);
            Assert.Null(agg.ClientType);
            Assert.Null(agg.ConsentType);
            Assert.Null(agg.ApplicationType);
            Assert.Empty(agg.RedirectUris);
        }
    }

    public class Setters
    {
        [Fact]
        public void Each_setter_returns_event_with_aggregate_id_and_mutates_state()
        {
            var (agg, _) = MakeDefault();

            var dn = agg.SetDisplayName("New Name");
            Assert.Equal(agg.Id, dn.ApplicationId);
            Assert.Equal("New Name", agg.DisplayName);

            var ct = agg.SetClientType("public");
            Assert.Equal("public", agg.ClientType);

            var cst = agg.SetConsentType("implicit");
            Assert.Equal("implicit", agg.ConsentType);

            var ru = agg.SetRedirectUris(new[] { "https://a", "https://b" });
            Assert.Equal(new[] { "https://a", "https://b" }, agg.RedirectUris);

            var plru = agg.SetPostLogoutRedirectUris(new[] { "https://logout" });
            Assert.Equal(new[] { "https://logout" }, agg.PostLogoutRedirectUris);

            var perms = agg.SetPermissions(new[] { "ept:authorization" });
            Assert.Equal(new[] { "ept:authorization" }, agg.Permissions);

            var req = agg.SetRequirements(new[] { "ft:pkce", "ft:dpop" });
            Assert.Equal(new[] { "ft:pkce", "ft:dpop" }, agg.Requirements);

            var s = agg.SetSettings(new Dictionary<string, string> { ["k"] = "v" });
            Assert.Equal("v", agg.Settings["k"]);

            var dns = agg.SetDisplayNames(new Dictionary<string, string> { ["de"] = "Hallo" });
            Assert.Equal("Hallo", agg.DisplayNames["de"]);

            var props = agg.SetProperties(new Dictionary<string, object?> { ["x"] = 42 });
            Assert.Equal(42, agg.Properties["x"]);
        }

        [Fact]
        public void Accepts_empty_lists_and_dicts()
        {
            var (agg, _) = MakeDefault();
            agg.SetRedirectUris(Array.Empty<string>());
            agg.SetSettings(new Dictionary<string, string>());
            Assert.Empty(agg.RedirectUris);
            Assert.Empty(agg.Settings);
        }

        [Fact]
        public void Setters_allow_null_for_optional_strings()
        {
            var (agg, _) = MakeDefault();
            agg.SetDisplayName(null);
            agg.SetClientType(null);
            agg.SetConsentType(null);
            Assert.Null(agg.DisplayName);
            Assert.Null(agg.ClientType);
            Assert.Null(agg.ConsentType);
        }

        [Fact]
        public void List_setter_takes_defensive_copy_so_caller_mutation_does_not_leak()
        {
            var (agg, _) = MakeDefault();
            var input = new List<string> { "https://a" };
            agg.SetRedirectUris(input);

            input.Add("https://b");

            Assert.Single(agg.RedirectUris);
            Assert.Equal("https://a", agg.RedirectUris[0]);
        }

        [Fact]
        public void Dict_setter_takes_defensive_copy_so_caller_mutation_does_not_leak()
        {
            var (agg, _) = MakeDefault();
            var input = new Dictionary<string, string> { ["k"] = "v" };
            agg.SetSettings(input);

            input["k"] = "mutated";
            input["k2"] = "added";

            Assert.Equal("v", agg.Settings["k"]);
            Assert.False(agg.Settings.ContainsKey("k2"));
        }

        [Fact]
        public void SetLinkedServiceAccountId_round_trips_and_clears()
        {
            var (agg, _) = MakeDefault();
            var saId = Guid.NewGuid();

            var setEvent = agg.SetLinkedServiceAccountId(saId);
            Assert.Equal(agg.Id, setEvent.ApplicationId);
            Assert.Equal(saId, setEvent.ServiceAccountId);
            Assert.Equal(saId, agg.LinkedServiceAccountId);

            var clearEvent = agg.SetLinkedServiceAccountId(null);
            Assert.Null(clearEvent.ServiceAccountId);
            Assert.Null(agg.LinkedServiceAccountId);
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
            Assert.Equal(agg.Id, e.ApplicationId);
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
            // Documents current behavior: aggregate has no post-delete write guard.
            // Validation lives in the application layer; the aggregate is dumb on purpose.
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
            var (original, created) = OAuthApplicationAggregate.Create(
                id, "client", "Client", "confidential", "explicit", "web",
                new[] { "https://a" }, new[] { "https://logout" },
                new[] { "ept:token" }, new[] { "ft:pkce" });

            var e1 = original.SetDisplayName("Updated");
            var e2 = original.SetRedirectUris(new[] { "https://x", "https://y" });
            var e3 = original.SetSettings(new Dictionary<string, string> { ["a"] = "1" });
            var e4 = original.SetProperties(new Dictionary<string, object?> { ["p"] = "v" });

            var replay = new OAuthApplicationAggregate();
            replay.Apply(created);
            replay.Apply(e1);
            replay.Apply(e2);
            replay.Apply(e3);
            replay.Apply(e4);

            Assert.Equal(original.Id, replay.Id);
            Assert.Equal(original.ClientId, replay.ClientId);
            Assert.Equal(original.DisplayName, replay.DisplayName);
            Assert.Equal(original.RedirectUris, replay.RedirectUris);
            Assert.Equal(original.Settings, replay.Settings);
            Assert.Equal(original.Properties, replay.Properties);
            Assert.Equal(original.IsDeleted, replay.IsDeleted);
        }

        [Fact]
        public void Replaying_delete_event_marks_aggregate_deleted()
        {
            var id = Guid.NewGuid();
            var (_, created) = MakeDefault(id);

            var replay = new OAuthApplicationAggregate();
            replay.Apply(created);
            replay.Apply(new OAuthApplicationDeleted(id));

            Assert.True(replay.IsDeleted);
        }
    }
}
