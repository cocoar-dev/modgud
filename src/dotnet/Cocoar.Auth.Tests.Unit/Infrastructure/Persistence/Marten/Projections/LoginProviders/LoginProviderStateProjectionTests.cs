using Cocoar.Auth.Domain.Identity.LoginProviders;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.LoginProviders;

namespace Cocoar.Auth.Tests.Unit.Infrastructure.Persistence.Marten.Projections.LoginProviders;

/// <summary>
/// Pins the inline projection rules for <see cref="LoginProviderState"/>.
/// </summary>
public class LoginProviderStateProjectionTests
{
    private static LoginProviderState NewState() =>
        new LoginProviderStateProjection().Create(new LoginProviderCreated(
            Guid.NewGuid(),
            Name: "google",
            DisplayName: "Google",
            Description: "Sign in with Google",
            Type: LoginProviderType.OpenIdConnect,
            Configuration: new Dictionary<string, string> { ["clientId"] = "abc" },
            IsBuiltIn: false));

    public class Create
    {
        [Fact]
        public void Initialises_all_fields_from_event()
        {
            var id = Guid.NewGuid();
            var s = new LoginProviderStateProjection().Create(new LoginProviderCreated(
                id, "internal", "Internal", "Built-in",
                LoginProviderType.Internal,
                new Dictionary<string, string> { ["k"] = "v" },
                IsBuiltIn: true));

            Assert.Equal(id, s.Id);
            Assert.Equal("internal", s.Name);
            Assert.Equal("Internal", s.DisplayName);
            Assert.Equal("Built-in", s.Description);
            Assert.Equal(LoginProviderType.Internal, s.Type);
            Assert.True(s.IsBuiltIn);
            Assert.Equal("v", s.Configuration["k"]);
            Assert.False(s.IsDeleted);
        }

        [Fact]
        public void Configuration_is_copied_not_aliased()
        {
            // The state owns its own dictionary so source mutations don't
            // bleed into projection state across event replays.
            var src = new Dictionary<string, string> { ["k"] = "v" };
            var s = new LoginProviderStateProjection().Create(new LoginProviderCreated(
                Guid.NewGuid(), "n", null, null, LoginProviderType.Internal, src, false));

            src["k"] = "MUTATED";

            Assert.Equal("v", s.Configuration["k"]);
        }
    }

    public class Apply
    {
        [Fact]
        public void NameChanged_updates_field()
        {
            var p = new LoginProviderStateProjection();
            var s = NewState();
            p.Apply(new LoginProviderNameChanged(s.Id, "google-v2"), s);
            Assert.Equal("google-v2", s.Name);
        }

        [Fact]
        public void DisplayNameChanged_supports_null()
        {
            var p = new LoginProviderStateProjection();
            var s = NewState();
            p.Apply(new LoginProviderDisplayNameChanged(s.Id, null), s);
            Assert.Null(s.DisplayName);
        }

        [Fact]
        public void DescriptionChanged_supports_null()
        {
            var p = new LoginProviderStateProjection();
            var s = NewState();
            p.Apply(new LoginProviderDescriptionChanged(s.Id, null), s);
            Assert.Null(s.Description);
        }

        [Fact]
        public void ConfigurationChanged_replaces_dictionary()
        {
            var p = new LoginProviderStateProjection();
            var s = NewState();
            p.Apply(new LoginProviderConfigurationChanged(s.Id,
                new Dictionary<string, string> { ["clientSecret"] = "shh" }), s);

            Assert.Single(s.Configuration);
            Assert.Equal("shh", s.Configuration["clientSecret"]);
            Assert.False(s.Configuration.ContainsKey("clientId"));
        }

        [Fact]
        public void Deleted_sets_IsDeleted_flag()
        {
            var p = new LoginProviderStateProjection();
            var s = NewState();
            p.Apply(new LoginProviderDeleted(s.Id), s);
            Assert.True(s.IsDeleted);
        }
    }
}
