using System.Text.Json;
using BuildingBlocks.Helper;
using Cocoar.Auth.Authentication.Api.Account;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Domain.Common;

namespace Cocoar.Auth.Tests.Unit.Authentication.Account;

/// <summary>
/// Pinning tests for the pure helpers behind <see cref="ProfileEndpoints"/>'s
/// self-service profile-change pipeline. The merge + cleanup chain is the only
/// barrier between "user submits a partial PATCH" and "admin approves the
/// merged payload" — a regression here silently approves edits a user never
/// submitted, or strips edits they did submit.
/// </summary>
public class ProfileEndpointsHelpersTests
{
    public class NormalizeOptional
    {
        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData("\t\n", null)]
        [InlineData("Alice", "Alice")]
        [InlineData("  Alice  ", "Alice")]
        public void Whitespace_collapses_to_null_otherwise_trimmed(string? raw, string? expected)
        {
            Assert.Equal(expected, ProfileEndpoints.NormalizeOptional(raw));
        }
    }

    public class StringEq
    {
        [Theory]
        [InlineData(null, null, true)]
        [InlineData("", "", true)]
        [InlineData(null, "", true)]      // both null-equivalent
        [InlineData("", null, true)]
        [InlineData("Alice", "Alice", true)]
        [InlineData("Alice", "alice", false)]   // case-sensitive
        [InlineData("Alice", "Bob", false)]
        [InlineData("", "Alice", false)]
        public void Treats_null_and_empty_as_equal_otherwise_case_sensitive(
            string? a, string? b, bool expected)
        {
            Assert.Equal(expected, ProfileEndpoints.StringEq(a, b));
        }
    }

    public class DeserializeProfile
    {
        [Fact]
        public void Empty_string_returns_fresh_dto_with_no_fields_set()
        {
            var p = ProfileEndpoints.DeserializeProfile("");

            Assert.False(p.Firstname.HasValue);
            Assert.False(p.Lastname.HasValue);
            Assert.False(p.Acronym.HasValue);
            Assert.False(p.Email.HasValue);
        }

        [Fact]
        public void Empty_object_returns_fresh_dto_with_no_fields_set()
        {
            var p = ProfileEndpoints.DeserializeProfile("{}");

            Assert.False(p.Firstname.HasValue);
            Assert.False(p.Email.HasValue);
        }

        [Fact]
        public void Parses_set_fields_into_optional_HasValue_state()
        {
            var p = ProfileEndpoints.DeserializeProfile(
                """{"Firstname":"Alice","Email":"alice@example.com"}""");

            Assert.True(p.Firstname.HasValue);
            Assert.Equal("Alice", p.Firstname.Value);
            Assert.True(p.Email.HasValue);
            Assert.Equal("alice@example.com", p.Email.Value);

            Assert.False(p.Lastname.HasValue);
            Assert.False(p.Acronym.HasValue);
        }

        [Fact]
        public void Json_explicit_null_yields_HasValue_with_null_value_for_nullable_optional()
        {
            // Optional<string?> Acronym/Email — sending JSON null is a real
            // distinction from "field absent". The first means "clear it",
            // the second means "leave alone".
            var p = ProfileEndpoints.DeserializeProfile("""{"Acronym":null}""");

            Assert.True(p.Acronym.HasValue);
            Assert.Null(p.Acronym.Value);
        }
    }

    public class MergeJson
    {
        [Fact]
        public void Submitted_field_overwrites_existing_field()
        {
            var existing = """{"Firstname":"Old"}""";
            var submission = new ProfileUpdateDto { Firstname = "New" };

            var merged = ProfileEndpoints.MergeJson(existing, submission);
            var p = ProfileEndpoints.DeserializeProfile(merged);

            Assert.True(p.Firstname.HasValue);
            Assert.Equal("New", p.Firstname.Value);
        }

        [Fact]
        public void Field_not_in_submission_is_preserved_from_existing()
        {
            // The whole point of partial PATCH: a previously-submitted field
            // must survive a follow-up submission that didn't include it.
            // Otherwise users would lose pending edits with every save.
            var existing = """{"Firstname":"Alice","Lastname":"Smith"}""";
            var submission = new ProfileUpdateDto { Lastname = "Jones" };

            var merged = ProfileEndpoints.MergeJson(existing, submission);
            var p = ProfileEndpoints.DeserializeProfile(merged);

            Assert.True(p.Firstname.HasValue);
            Assert.Equal("Alice", p.Firstname.Value);
            Assert.Equal("Jones", p.Lastname.Value);
        }

        [Fact]
        public void Empty_existing_payload_falls_back_to_submission_only()
        {
            var submission = new ProfileUpdateDto { Firstname = "Alice" };

            var merged = ProfileEndpoints.MergeJson("{}", submission);
            var p = ProfileEndpoints.DeserializeProfile(merged);

            Assert.Equal("Alice", p.Firstname.Value);
        }

        [Fact]
        public void Empty_submission_preserves_every_existing_field()
        {
            // No-op submission round-trip — equivalent to the user re-saving
            // the form without changing any field. Existing pending payload
            // must come back byte-equivalent.
            var existing = """{"Firstname":"Alice","Email":"alice@example.com"}""";

            var merged = ProfileEndpoints.MergeJson(existing, new ProfileUpdateDto());
            var p = ProfileEndpoints.DeserializeProfile(merged);

            Assert.Equal("Alice", p.Firstname.Value);
            Assert.Equal("alice@example.com", p.Email.Value);
        }
    }

    public class CleanupProfilePayload
    {
        private static ApplicationUser User(
            string? firstname = null, string? lastname = null,
            string? acronym = null, string? email = null) => new()
        {
            UserName = "u",
            Firstname = firstname ?? "",
            Lastname = lastname ?? "",
            Acronym = acronym,
            Email = email,
        };

        [Fact]
        public void Drops_field_matching_users_current_value()
        {
            // User Firstname already "Alice"; re-submitting "Alice" is a no-op.
            // Cleanup must turn it into Optional.None so the change-request
            // doesn't carry a no-op approval forward.
            var payload = """{"Firstname":"Alice"}""";
            var (json, hasAny) = ProfileEndpoints.CleanupProfilePayload(
                payload, User(firstname: "Alice"));

            var p = ProfileEndpoints.DeserializeProfile(json);
            Assert.False(p.Firstname.HasValue);
            Assert.False(hasAny);
        }

        [Fact]
        public void Keeps_field_that_actually_differs_from_user()
        {
            var payload = """{"Firstname":"Bob"}""";
            var (json, hasAny) = ProfileEndpoints.CleanupProfilePayload(
                payload, User(firstname: "Alice"));

            var p = ProfileEndpoints.DeserializeProfile(json);
            Assert.True(p.Firstname.HasValue);
            Assert.Equal("Bob", p.Firstname.Value);
            Assert.True(hasAny);
        }

        [Fact]
        public void Empty_string_on_user_treated_as_match_for_null_submission()
        {
            // Identity stores "" for unset fields. The user submits null
            // (Optional<string?>) for Acronym → no change. StringEq treats
            // "" and null as equal so the field gets dropped.
            var payload = """{"Acronym":null}""";
            var (json, hasAny) = ProfileEndpoints.CleanupProfilePayload(
                payload, User(acronym: ""));

            var p = ProfileEndpoints.DeserializeProfile(json);
            Assert.False(p.Acronym.HasValue);
            Assert.False(hasAny);
        }

        [Fact]
        public void HasAny_stays_true_when_at_least_one_field_actually_changes()
        {
            // Mixed payload — Firstname is no-op, Lastname is real change.
            var payload = """{"Firstname":"Alice","Lastname":"NewLast"}""";
            var (json, hasAny) = ProfileEndpoints.CleanupProfilePayload(
                payload, User(firstname: "Alice", lastname: "OldLast"));

            var p = ProfileEndpoints.DeserializeProfile(json);
            Assert.False(p.Firstname.HasValue);
            Assert.True(p.Lastname.HasValue);
            Assert.Equal("NewLast", p.Lastname.Value);
            Assert.True(hasAny);
        }

        [Fact]
        public void Every_field_a_noop_yields_HasAny_false_so_caller_can_short_circuit()
        {
            var payload = """{"Firstname":"Alice","Lastname":"Smith","Acronym":"AS","Email":"a@x"}""";
            var (json, hasAny) = ProfileEndpoints.CleanupProfilePayload(
                payload, User(firstname: "Alice", lastname: "Smith", acronym: "AS", email: "a@x"));

            Assert.False(hasAny);
            var p = ProfileEndpoints.DeserializeProfile(json);
            Assert.False(p.Firstname.HasValue);
            Assert.False(p.Lastname.HasValue);
            Assert.False(p.Acronym.HasValue);
            Assert.False(p.Email.HasValue);
        }
    }

    public class EnumerateProfileChanges
    {
        [Fact]
        public void Yields_only_HasValue_fields_with_user_value_as_old()
        {
            var payload = """{"Firstname":"NewFirst","Email":"new@x"}""";
            var user = new ApplicationUser
            {
                UserName = "u",
                Firstname = "OldFirst",
                Lastname = "Smith",
                Email = "old@x",
            };

            var changes = ProfileEndpoints.EnumerateProfileChanges(payload, user).ToList();

            Assert.Equal(2, changes.Count);

            var first = Assert.Single(changes, c => c.Field == "Firstname");
            Assert.Equal("OldFirst", first.OldValue);
            Assert.Equal("NewFirst", first.NewValue);

            var email = Assert.Single(changes, c => c.Field == "Email");
            Assert.Equal("old@x", email.OldValue);
            Assert.Equal("new@x", email.NewValue);
        }

        [Fact]
        public void Empty_payload_yields_no_entries()
        {
            var changes = ProfileEndpoints.EnumerateProfileChanges("{}", new ApplicationUser { UserName = "u" });
            Assert.Empty(changes);
        }

        [Fact]
        public void Null_user_passes_through_so_admin_view_works_when_user_was_deleted()
        {
            // Admins reviewing a request after the requesting user is gone
            // should still see the change list — OldValue just becomes null.
            var payload = """{"Firstname":"Alice"}""";
            var changes = ProfileEndpoints.EnumerateProfileChanges(payload, user: null).ToList();

            var c = Assert.Single(changes);
            Assert.Equal("Firstname", c.Field);
            Assert.Null(c.OldValue);
            Assert.Equal("Alice", c.NewValue);
        }
    }
}
