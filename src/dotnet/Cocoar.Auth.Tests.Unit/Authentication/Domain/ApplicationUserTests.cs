using Cocoar.Auth.Authentication.Domain;

namespace Cocoar.Auth.Tests.Unit.Authentication.Domain;

/// <summary>
/// Pins the constructor + <see cref="ApplicationUser.GetDisplayLabel"/> on the
/// authentication-side <c>ApplicationUser</c>. The display label is shown all
/// over the admin UI — the format ("ACR | Firstname Lastname") is part of the
/// product look.
/// </summary>
public class ApplicationUserTests
{
    public class Constructor
    {
        [Fact]
        public void Username_only_constructor_normalizes_username_to_upper()
        {
            var u = new ApplicationUser("Alice");

            Assert.Equal("Alice", u.UserName);
            Assert.Equal("ALICE", u.NormalizedUserName);
        }

        [Fact]
        public void Generates_a_non_empty_id()
        {
            var u = new ApplicationUser("alice");

            Assert.NotEqual(Guid.Empty, u.Id);
        }

        [Fact]
        public void Populates_security_and_concurrency_stamp()
        {
            var u = new ApplicationUser("alice");

            Assert.False(string.IsNullOrWhiteSpace(u.SecurityStamp));
            Assert.False(string.IsNullOrWhiteSpace(u.ConcurrencyStamp));
        }

        [Fact]
        public void Email_constructor_normalizes_email_to_upper()
        {
            var u = new ApplicationUser("alice", "alice@example.com");

            Assert.Equal("alice@example.com", u.Email);
            Assert.Equal("ALICE@EXAMPLE.COM", u.NormalizedEmail);
        }

        [Fact]
        public void Email_constructor_with_null_email_leaves_email_null()
        {
            var u = new ApplicationUser("alice", null);

            Assert.Null(u.Email);
            Assert.Null(u.NormalizedEmail);
        }

        [Fact]
        public void Parameterless_constructor_does_not_generate_id_or_stamps()
        {
            // Marten document hydration uses the parameterless ctor — the persisted
            // values must win, so the ctor must NOT pre-populate Id/stamps.
            var u = new ApplicationUser();

            Assert.Equal(Guid.Empty, u.Id);
            Assert.Null(u.SecurityStamp);
            Assert.Null(u.ConcurrencyStamp);
        }
    }

    public class GetDisplayLabel
    {
        [Fact]
        public void Combines_acronym_and_full_name_with_pipe()
        {
            var u = new ApplicationUser("alice")
            {
                Acronym = "AB",
                Firstname = "Alice",
                Lastname = "Bob",
            };

            Assert.Equal("AB | Alice Bob", u.GetDisplayLabel());
        }

        [Fact]
        public void Without_acronym_returns_full_name_only()
        {
            var u = new ApplicationUser("alice")
            {
                Firstname = "Alice",
                Lastname = "Bob",
            };

            Assert.Equal("Alice Bob", u.GetDisplayLabel());
        }

        [Fact]
        public void Without_lastname_returns_acronym_and_firstname()
        {
            var u = new ApplicationUser("alice")
            {
                Acronym = "AB",
                Firstname = "Alice",
            };

            Assert.Equal("AB | Alice", u.GetDisplayLabel());
        }

        [Fact]
        public void Without_any_name_returns_empty_string()
        {
            // Empty Firstname/Lastname/Acronym → result is "" rather than "  | "
            // because the per-part trim filters out the whitespace-only segment.
            var u = new ApplicationUser("alice");

            Assert.Equal(string.Empty, u.GetDisplayLabel());
        }

        [Fact]
        public void Whitespace_only_firstname_and_lastname_are_filtered_out()
        {
            var u = new ApplicationUser("alice")
            {
                Firstname = "  ",
                Lastname = "  ",
            };

            Assert.Equal(string.Empty, u.GetDisplayLabel());
        }

        [Fact]
        public void Whitespace_only_acronym_is_filtered_out()
        {
            var u = new ApplicationUser("alice")
            {
                Acronym = "   ",
                Firstname = "Alice",
                Lastname = "Bob",
            };

            Assert.Equal("Alice Bob", u.GetDisplayLabel());
        }
    }
}
