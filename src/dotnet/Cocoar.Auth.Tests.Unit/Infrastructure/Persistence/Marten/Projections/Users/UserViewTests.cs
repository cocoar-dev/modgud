using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users;

namespace Cocoar.Auth.Tests.Unit.Infrastructure.Persistence.Marten.Projections.Users;

/// <summary>
/// Pins the display-label fallback chain on <see cref="UserView.GetDisplayLabel"/>.
/// This label drives every row in the admin user grid — silent regressions show
/// up only at QA time, and the cascade (Acronym | Firstname Lastname → UserName)
/// is intentional product behaviour.
/// </summary>
public class UserViewTests
{
    public class GetDisplayLabel
    {
        [Fact]
        public void Joins_acronym_and_full_name_with_pipe()
        {
            var view = new UserView
            {
                Acronym = "AB",
                Firstname = "Alice",
                Lastname = "Brown",
                UserName = "alice",
            };

            Assert.Equal("AB | Alice Brown", view.GetDisplayLabel());
        }

        [Fact]
        public void Falls_back_to_full_name_when_no_acronym()
        {
            var view = new UserView { Firstname = "Alice", Lastname = "Brown", UserName = "alice" };
            Assert.Equal("Alice Brown", view.GetDisplayLabel());
        }

        [Fact]
        public void Falls_back_to_username_when_no_name_parts()
        {
            var view = new UserView { UserName = "alice" };
            Assert.Equal("alice", view.GetDisplayLabel());
        }

        [Fact]
        public void Returns_placeholder_when_nothing_set()
        {
            // Empty/missing user always yields something visible — admin grids
            // never render a blank row from this method.
            var view = new UserView();
            Assert.Equal("<no name>", view.GetDisplayLabel());
        }

        [Fact]
        public void Trims_partial_name_with_only_firstname()
        {
            var view = new UserView { Firstname = "Alice" };
            Assert.Equal("Alice", view.GetDisplayLabel());
        }

        [Fact]
        public void Trims_partial_name_with_only_lastname()
        {
            var view = new UserView { Lastname = "Brown" };
            Assert.Equal("Brown", view.GetDisplayLabel());
        }

        [Fact]
        public void Whitespace_acronym_is_treated_as_missing()
        {
            var view = new UserView
            {
                Acronym = "   ",
                Firstname = "Alice",
                Lastname = "Brown",
            };
            Assert.Equal("Alice Brown", view.GetDisplayLabel());
        }

        [Fact]
        public void Whitespace_username_falls_through_to_placeholder()
        {
            // A whitespace-only UserName must not surface as a blank admin row.
            // Same fall-through as the all-empty case.
            var view = new UserView { UserName = "   " };
            Assert.Equal("<no name>", view.GetDisplayLabel());
        }
    }
}
