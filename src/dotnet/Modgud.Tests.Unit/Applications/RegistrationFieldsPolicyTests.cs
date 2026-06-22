using Modgud.Domain.Realms;

namespace Modgud.Tests.Unit.Applications;

/// <summary>
/// Pins the pure enforcement of <see cref="RegistrationFieldsSettings"/> shared by
/// every account-creation path: which required field (if any) is missing, and how
/// the effective username is resolved from the Off/Optional/Required policy.
/// </summary>
public class RegistrationFieldsPolicyTests
{
    public class FirstMissingRequired
    {
        [Fact]
        public void Null_settings_default_to_all_optional_so_nothing_is_required()
        {
            var missing = RegistrationFieldsPolicy.FirstMissingRequired(
                settings: null, username: null, firstname: null, lastname: null);

            Assert.Null(missing);
        }

        [Fact]
        public void Required_name_missing_is_reported()
        {
            var settings = new RegistrationFieldsSettings { Firstname = FieldRequirement.Required };

            var missing = RegistrationFieldsPolicy.FirstMissingRequired(settings, "user", firstname: "  ", lastname: "x");

            Assert.Equal(RegistrationField.Firstname, missing);
        }

        [Fact]
        public void Required_field_present_passes()
        {
            var settings = new RegistrationFieldsSettings
            {
                Username = FieldRequirement.Required,
                Firstname = FieldRequirement.Required,
                Lastname = FieldRequirement.Required,
            };

            var missing = RegistrationFieldsPolicy.FirstMissingRequired(settings, "u", "f", "l");

            Assert.Null(missing);
        }

        [Fact]
        public void Username_checked_first_then_firstname_then_lastname()
        {
            var settings = new RegistrationFieldsSettings
            {
                Username = FieldRequirement.Required,
                Firstname = FieldRequirement.Required,
                Lastname = FieldRequirement.Required,
            };

            Assert.Equal(RegistrationField.Username,
                RegistrationFieldsPolicy.FirstMissingRequired(settings, "", "", ""));
            Assert.Equal(RegistrationField.Firstname,
                RegistrationFieldsPolicy.FirstMissingRequired(settings, "u", "", ""));
            Assert.Equal(RegistrationField.Lastname,
                RegistrationFieldsPolicy.FirstMissingRequired(settings, "u", "f", ""));
        }
    }

    public class FirstMissingRequiredName
    {
        [Fact]
        public void Never_enforces_username_even_when_required()
        {
            // Native paths use the email as the username, so a Username=Required
            // policy must not block them.
            var settings = new RegistrationFieldsSettings { Username = FieldRequirement.Required };

            var missing = RegistrationFieldsPolicy.FirstMissingRequiredName(settings, firstname: "f", lastname: "l");

            Assert.Null(missing);
        }

        [Fact]
        public void Enforces_required_names()
        {
            var settings = new RegistrationFieldsSettings { Lastname = FieldRequirement.Required };

            var missing = RegistrationFieldsPolicy.FirstMissingRequiredName(settings, firstname: "f", lastname: null);

            Assert.Equal(RegistrationField.Lastname, missing);
        }
    }

    public class ResolveUsername
    {
        [Fact]
        public void Off_always_returns_the_email()
        {
            var settings = new RegistrationFieldsSettings { Username = FieldRequirement.Off };

            Assert.Equal("a@b.com", RegistrationFieldsPolicy.ResolveUsername(settings, "ignored", "a@b.com"));
        }

        [Fact]
        public void Optional_blank_falls_back_to_email()
        {
            var settings = new RegistrationFieldsSettings { Username = FieldRequirement.Optional };

            Assert.Equal("a@b.com", RegistrationFieldsPolicy.ResolveUsername(settings, "   ", "a@b.com"));
        }

        [Fact]
        public void Optional_supplied_username_is_used()
        {
            var settings = new RegistrationFieldsSettings { Username = FieldRequirement.Optional };

            Assert.Equal("alice", RegistrationFieldsPolicy.ResolveUsername(settings, " alice ", "a@b.com"));
        }

        [Fact]
        public void Required_uses_the_supplied_username()
        {
            var settings = new RegistrationFieldsSettings { Username = FieldRequirement.Required };

            Assert.Equal("alice", RegistrationFieldsPolicy.ResolveUsername(settings, "alice", "a@b.com"));
        }

        [Fact]
        public void Null_settings_behave_as_optional()
        {
            Assert.Equal("a@b.com", RegistrationFieldsPolicy.ResolveUsername(null, null, "a@b.com"));
            Assert.Equal("bob", RegistrationFieldsPolicy.ResolveUsername(null, "bob", "a@b.com"));
        }
    }
}
