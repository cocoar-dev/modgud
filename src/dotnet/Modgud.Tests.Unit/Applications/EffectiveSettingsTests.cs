using Modgud.Domain.Applications;
using Modgud.Domain.Realms;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Tests.Unit.Applications;

/// <summary>
/// Pins the ADR-0011 settings cascade: <see cref="EffectiveSettings.From"/> is
/// the zero-behaviour path (no Application → realm settings unchanged), and
/// <see cref="EffectiveSettings.Merge"/> is the sparse, field-by-field override
/// (App value when set, else realm value).
/// </summary>
public class EffectiveSettingsTests
{
    private static RealmSettingsDoc Realm() => new()
    {
        SelfRegistration = new SelfRegistrationSettings { Enabled = true, RequireAdminApproval = true },
        Dcr = new DcrSettings { Enabled = true },
        Cimd = new CimdSettings { Enabled = true },
        NativeGrants = new NativeGrantSettings
        {
            Enabled = false,
            AccessTokenLifetime = TimeSpan.FromMinutes(15),
            RefreshTokenLifetime = TimeSpan.FromDays(14),
        },
        Branding = new BrandingSettings { ProductName = "RealmProduct", PrimaryColor = "#111111" },
        RegistrationFields = new RegistrationFieldsSettings
        {
            Username = FieldRequirement.Off,
            Firstname = FieldRequirement.Required,
            Lastname = FieldRequirement.Optional,
        },
        Deletion = DeletionSettings.Defaults,
        Audit = AuditSettings.Defaults,
        Pages = new Dictionary<string, string> { ["login"] = "{}" },
    };

    public class From
    {
        [Fact]
        public void Returns_every_realm_section_unchanged()
        {
            var realm = Realm();

            var eff = EffectiveSettings.From(realm);

            Assert.Equal(realm.SelfRegistration, eff.SelfRegistration);
            Assert.Equal(realm.Dcr, eff.Dcr);
            Assert.Equal(realm.Cimd, eff.Cimd);
            Assert.Equal(realm.NativeGrants, eff.NativeGrants);
            Assert.Equal(realm.Branding, eff.Branding);
            Assert.Equal(realm.RegistrationFields, eff.RegistrationFields);
            Assert.Equal(realm.Deletion, eff.Deletion);
            Assert.Equal(realm.Audit, eff.Audit);
            Assert.Equal(realm.Pages, eff.Pages);
        }

        [Fact]
        public void Leaves_application_facets_at_no_application_values()
        {
            var eff = EffectiveSettings.From(Realm());

            Assert.Null(eff.SelfRegPosture); // no Application → legacy realm-only registration
            Assert.Null(eff.Origin);
            Assert.Null(eff.EmailBranding);
        }

        [Fact]
        public void Empty_realm_resolves_all_null_sections()
        {
            var eff = EffectiveSettings.From(new RealmSettingsDoc());

            Assert.Null(eff.SelfRegistration);
            Assert.Null(eff.NativeGrants);
            Assert.Null(eff.Branding);
            Assert.Null(eff.SelfRegPosture);
        }
    }

    public class Merge
    {
        [Fact]
        public void Empty_application_keeps_every_realm_section()
        {
            var realm = Realm();

            var eff = EffectiveSettings.Merge(realm, new ApplicationSettings());

            Assert.Equal(realm.SelfRegistration, eff.SelfRegistration);
            Assert.Equal(realm.Dcr, eff.Dcr);
            Assert.Equal(realm.Cimd, eff.Cimd);
            Assert.Equal(realm.NativeGrants, eff.NativeGrants);
            Assert.Equal(realm.Branding, eff.Branding);
            Assert.Equal(realm.RegistrationFields, eff.RegistrationFields);
            Assert.Equal(realm.Deletion, eff.Deletion);
            Assert.Equal(realm.Audit, eff.Audit);
            Assert.Equal(realm.Pages, eff.Pages);
        }

        [Fact]
        public void Empty_application_defaults_posture_to_jit()
        {
            var eff = EffectiveSettings.Merge(Realm(), new ApplicationSettings());

            Assert.Equal(SelfRegPosture.JitOnOtp, eff.SelfRegPosture);
        }

        [Fact]
        public void Explicit_posture_overrides_the_default()
        {
            var app = new ApplicationSettings
            {
                SelfRegistration = new ApplicationSelfRegistration { Posture = SelfRegPosture.Off },
            };

            var eff = EffectiveSettings.Merge(Realm(), app);

            Assert.Equal(SelfRegPosture.Off, eff.SelfRegPosture);
        }

        [Fact]
        public void Branding_is_merged_field_by_field()
        {
            var app = new ApplicationSettings
            {
                // Override only the product name; primary color must inherit the realm.
                Branding = new BrandingSettings { ProductName = "AppProduct" },
            };

            var eff = EffectiveSettings.Merge(Realm(), app);

            Assert.Equal("AppProduct", eff.Branding!.ProductName);
            Assert.Equal("#111111", eff.Branding.PrimaryColor);
        }

        [Fact]
        public void Native_grants_are_merged_field_by_field()
        {
            var app = new ApplicationSettings
            {
                // Flip Enabled on; lifetimes must inherit the realm values.
                NativeGrants = new ApplicationNativeGrantOverrides { Enabled = true },
            };

            var eff = EffectiveSettings.Merge(Realm(), app);

            Assert.True(eff.NativeGrants!.Enabled);
            Assert.Equal(TimeSpan.FromMinutes(15), eff.NativeGrants.AccessTokenLifetime);
            Assert.Equal(TimeSpan.FromDays(14), eff.NativeGrants.RefreshTokenLifetime);
        }

        [Fact]
        public void Native_grants_override_against_unset_realm_section_uses_record_defaults()
        {
            var realm = new RealmSettingsDoc(); // NativeGrants null = realm defaults
            var app = new ApplicationSettings
            {
                NativeGrants = new ApplicationNativeGrantOverrides { Enabled = true },
            };

            var eff = EffectiveSettings.Merge(realm, app);

            Assert.True(eff.NativeGrants!.Enabled);
            // Inherited from the NativeGrantSettings record defaults, not realm config.
            Assert.Equal(TimeSpan.FromMinutes(15), eff.NativeGrants.AccessTokenLifetime);
            Assert.Equal(TimeSpan.FromDays(14), eff.NativeGrants.RefreshTokenLifetime);
        }

        [Fact]
        public void Null_override_sections_pass_realm_branding_through_including_null()
        {
            var realm = new RealmSettingsDoc { Branding = null };

            var eff = EffectiveSettings.Merge(realm, new ApplicationSettings());

            Assert.Null(eff.Branding);
        }

        [Fact]
        public void Self_registration_is_merged_field_by_field()
        {
            var app = new ApplicationSettings
            {
                // Override Enabled only; RequireAdminApproval must inherit the realm.
                SelfRegistration = new ApplicationSelfRegistration { Enabled = false },
            };

            var eff = EffectiveSettings.Merge(Realm(), app);

            Assert.False(eff.SelfRegistration!.Enabled);
            Assert.True(eff.SelfRegistration.RequireAdminApproval); // inherited
        }

        [Fact]
        public void Dcr_is_merged_field_by_field()
        {
            var app = new ApplicationSettings
            {
                Dcr = new ApplicationDcrOverrides { AccessTokenLifetime = TimeSpan.FromMinutes(5) },
            };

            var eff = EffectiveSettings.Merge(Realm(), app);

            Assert.True(eff.Dcr!.Enabled); // inherited (realm = true)
            Assert.Equal(TimeSpan.FromMinutes(5), eff.Dcr.AccessTokenLifetime); // overridden
        }

        [Fact]
        public void Cimd_is_merged_field_by_field()
        {
            var app = new ApplicationSettings
            {
                Cimd = new ApplicationCimdOverrides { Enabled = false },
            };

            var eff = EffectiveSettings.Merge(Realm(), app);

            Assert.False(eff.Cimd!.Enabled); // overridden
        }

        [Fact]
        public void Registration_fields_are_merged_field_by_field()
        {
            var app = new ApplicationSettings
            {
                // Override only Username; the name requirements must inherit the realm.
                RegistrationFields = new ApplicationRegistrationFieldsOverrides
                {
                    Username = FieldRequirement.Required,
                },
            };

            var eff = EffectiveSettings.Merge(Realm(), app);

            Assert.Equal(FieldRequirement.Required, eff.RegistrationFields!.Username); // overridden
            Assert.Equal(FieldRequirement.Required, eff.RegistrationFields.Firstname); // inherited
            Assert.Equal(FieldRequirement.Optional, eff.RegistrationFields.Lastname);  // inherited
        }

        [Fact]
        public void Registration_fields_override_against_unset_realm_section_uses_record_defaults()
        {
            var realm = new RealmSettingsDoc(); // RegistrationFields null = all Optional
            var app = new ApplicationSettings
            {
                RegistrationFields = new ApplicationRegistrationFieldsOverrides
                {
                    Lastname = FieldRequirement.Required,
                },
            };

            var eff = EffectiveSettings.Merge(realm, app);

            Assert.Equal(FieldRequirement.Optional, eff.RegistrationFields!.Username);  // record default
            Assert.Equal(FieldRequirement.Optional, eff.RegistrationFields.Firstname);  // record default
            Assert.Equal(FieldRequirement.Required, eff.RegistrationFields.Lastname);   // overridden
        }

        [Fact]
        public void Origin_and_email_branding_pass_through_from_application()
        {
            var app = new ApplicationSettings
            {
                Origin = new ApplicationOrigin { Subdomain = "amzettel.cocoar.app" },
                EmailBranding = new ApplicationEmailBranding { ProductName = "amZettel" },
            };

            var eff = EffectiveSettings.Merge(Realm(), app);

            Assert.Equal("amzettel.cocoar.app", eff.Origin!.Subdomain);
            Assert.Equal("amZettel", eff.EmailBranding!.ProductName);
        }
    }
}
