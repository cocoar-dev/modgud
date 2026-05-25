using Modgud.Authorization.Principals;

namespace Modgud.Tests.Unit.Authorization.Principals;

/// <summary>
/// Pins the pure-function surface of <see cref="Person"/>: the computed
/// <see cref="Person.DisplayName"/> fallback chain and the email-resolution
/// behaviour. Identity-fragment changes here are visible end-to-end (admin
/// grids, audit logs, notifications) — every branch of the fallback gets a test.
/// </summary>
public class PersonTests
{
    public class Type
    {
        [Fact]
        public void Is_stable_discriminator_string()
        {
            // The discriminator drives Marten sub-class mapping AND JsEval predicate
            // matching — must never accidentally drift to nameof(Person) etc.
            var p = new Person();
            Assert.Equal("person", p.Type);
        }
    }

    public class DisplayName
    {
        [Fact]
        public void Combines_acronym_and_full_name_with_pipe_separator()
        {
            var p = new Person { Acronym = "JD", Firstname = "John", Lastname = "Doe" };
            Assert.Equal("JD | John Doe", p.DisplayName);
        }

        [Fact]
        public void Returns_full_name_when_acronym_missing()
        {
            var p = new Person { Firstname = "John", Lastname = "Doe" };
            Assert.Equal("John Doe", p.DisplayName);
        }

        [Fact]
        public void Returns_acronym_only_when_names_missing()
        {
            var p = new Person { Acronym = "JD" };
            Assert.Equal("JD", p.DisplayName);
        }

        [Fact]
        public void Returns_firstname_only_when_lastname_missing()
        {
            var p = new Person { Firstname = "John" };
            Assert.Equal("John", p.DisplayName);
        }

        [Fact]
        public void Returns_lastname_only_when_firstname_missing()
        {
            // The internal trim collapses the leading empty firstname slot, so the
            // visible label is just "Doe" — not " Doe" with a leading space.
            var p = new Person { Lastname = "Doe" };
            Assert.Equal("Doe", p.DisplayName);
        }

        [Fact]
        public void Falls_back_to_account_name_when_no_identity_fields_set()
        {
            var p = new Person { AccountName = "jdoe" };
            Assert.Equal("jdoe", p.DisplayName);
        }

        [Fact]
        public void Falls_back_to_id_when_account_name_also_missing()
        {
            var id = Guid.NewGuid();
            var p = new Person { Id = id };
            Assert.Equal(id.ToString(), p.DisplayName);
        }

        [Fact]
        public void Whitespace_only_fields_are_treated_as_empty()
        {
            // Critical because admin UIs may submit empty-feeling strings; the
            // fallback chain must not produce a string of just spaces and pipes.
            var p = new Person
            {
                Acronym = "   ",
                Firstname = "  ",
                Lastname = "\t",
                AccountName = "jdoe",
            };
            Assert.Equal("jdoe", p.DisplayName);
        }

        [Fact]
        public void Empty_string_fields_fall_through_to_account_name()
        {
            var p = new Person
            {
                Acronym = "",
                Firstname = "",
                Lastname = "",
                AccountName = "jdoe",
            };
            Assert.Equal("jdoe", p.DisplayName);
        }
    }

    public class GetEmailsAsync
    {
        [Fact]
        public async Task Returns_single_address_when_email_set()
        {
            var p = new Person { Email = "john@example.com" };
            var emails = await p.GetEmailsAsync(new NullEmailContext(), TestContext.Current.CancellationToken);
            Assert.Equal(["john@example.com"], emails);
        }

        [Fact]
        public async Task Returns_empty_list_when_email_null()
        {
            var p = new Person();
            var emails = await p.GetEmailsAsync(new NullEmailContext(), TestContext.Current.CancellationToken);
            Assert.Empty(emails);
        }

        [Fact]
        public async Task Returns_empty_list_when_email_whitespace_only()
        {
            var p = new Person { Email = "   " };
            var emails = await p.GetEmailsAsync(new NullEmailContext(), TestContext.Current.CancellationToken);
            Assert.Empty(emails);
        }

        [Fact]
        public async Task Does_not_consult_resolution_context()
        {
            // A person resolves only their own address — must never try to load
            // other principals (would loop forever for nested-group resolution).
            var ctx = new ThrowingEmailContext();
            var p = new Person { Email = "x@y.z" };
            await p.GetEmailsAsync(ctx, TestContext.Current.CancellationToken);
            // Reaching here without exception proves the context was not invoked.
        }
    }

    private sealed class NullEmailContext : IEmailResolutionContext
    {
        public Task<IPrincipal?> LoadPrincipalAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<IPrincipal?>(null);
    }

    private sealed class ThrowingEmailContext : IEmailResolutionContext
    {
        public Task<IPrincipal?> LoadPrincipalAsync(Guid id, CancellationToken ct = default)
            => throw new InvalidOperationException("Person.GetEmailsAsync must not consult the context.");
    }
}
