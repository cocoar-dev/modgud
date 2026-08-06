using Modgud.Domain.Applications;
using Modgud.Domain.Realms;
using Modgud.Domain.RealmSettings;
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
        ClientSessions = new ClientSessionPolicy
        {
            IdleLifetime = TimeSpan.FromDays(30),
            AbsoluteLifetime = TimeSpan.FromDays(365),
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
            Assert.Equal(realm.ClientSessions, eff.ClientSessions);
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
            Assert.Null(eff.PageTheme);
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
        public void Realm_legacy_pages_pass_through_and_app_inherits()
        {
            // Un-migrated legacy realm schemas still resolve; an App with no
            // selection inherits them (Apps no longer author their own schemas).
            var realm = Realm();
            realm.Pages!["password-forgot"] = "realm-forgot";
            var app = new ApplicationSettings();

            var eff = EffectiveSettings.Merge(realm, app);

            Assert.Equal("realm-forgot", eff.Pages!["password-forgot"]);
        }

        [Fact]
        public void Application_only_presentation_facets_pass_through_from_application()
        {
            var app = new ApplicationSettings
            {
                Origin = new ApplicationOrigin { Subdomain = "amzettel.cocoar.app" },
                EmailBranding = new ApplicationEmailBranding { ProductName = "amZettel" },
                PageTheme = new ApplicationPageTheme
                {
                    AccentColor = "#10b981",
                    ButtonRadiusPx = 999,
                },
            };

            var eff = EffectiveSettings.Merge(Realm(), app);

            Assert.Equal("amzettel.cocoar.app", eff.Origin!.Subdomain);
            Assert.Equal("amZettel", eff.EmailBranding!.ProductName);
            Assert.Equal("#10b981", eff.PageTheme!.AccentColor);
            Assert.Equal(999, eff.PageTheme.ButtonRadiusPx);
        }

        [Fact]
        public void Email_branding_merges_application_fields_over_realm_defaults()
        {
            var realm = Realm();
            realm.EmailBranding = new EmailBrandingSettings
            {
                ProductName = "Realm Mail",
                SubjectPrefix = "Realm",
                Preheader = "Realm preheader",
                FooterText = "Realm footer",
                FromName = "Realm Sender",
                ReplyTo = "realm@example.test",
            };
            var app = new ApplicationSettings
            {
                EmailBranding = new ApplicationEmailBranding
                {
                    ProductName = "App Mail",
                    FooterText = "App footer",
                    FromName = "App Sender",
                },
            };

            var effective = EffectiveSettings.Merge(realm, app).EmailBranding!;

            Assert.Equal("App Mail", effective.ProductName);
            Assert.Equal("Realm", effective.SubjectPrefix);
            Assert.Equal("Realm preheader", effective.Preheader);
            Assert.Equal("App footer", effective.FooterText);
            Assert.Equal("App Sender", effective.FromName);
            Assert.Equal("realm@example.test", effective.ReplyTo);
        }

        // ─────────────── ADR-0001: variants + activation ───────────────

        private static RealmSettingsDoc RealmWithSlot(string slug, RealmPageSlot slot)
            => new() { PageSlots = new Dictionary<string, RealmPageSlot> { [slug] = slot } };

        [Fact]
        public void Realm_active_variant_resolves_to_its_schema()
        {
            var realm = RealmWithSlot("login", new RealmPageSlot
            {
                Variants =
                [
                    new PageVariant { Id = "a", Name = "A", Schema = "schema-a" },
                    new PageVariant { Id = "b", Name = "B", Schema = "schema-b" },
                ],
                ActiveVariantId = "b",
            });

            var eff = EffectiveSettings.From(realm);

            Assert.Equal("schema-b", eff.Pages!["login"]);
        }

        [Fact]
        public void Realm_builtin_when_no_active_variant_omits_the_slot()
        {
            // Variants exist but none is active → the slot falls back to the
            // built-in hardcoded view (absent from the effective Pages).
            var realm = RealmWithSlot("login", new RealmPageSlot
            {
                Variants = [new PageVariant { Id = "a", Name = "A", Schema = "schema-a" }],
                ActiveVariantId = null,
            });

            var eff = EffectiveSettings.From(realm);

            Assert.True(eff.Pages is null || !eff.Pages.ContainsKey("login"));
        }

        [Fact]
        public void App_inherits_realm_active_by_default()
        {
            var realm = RealmWithSlot("login", new RealmPageSlot
            {
                Variants = [new PageVariant { Id = "r", Name = "R", Schema = "realm-login" }],
                ActiveVariantId = "r",
            });
            var app = new ApplicationSettings(); // no PageSlots → inherit

            var eff = EffectiveSettings.Merge(realm, app);

            Assert.Equal("realm-login", eff.Pages!["login"]);
        }

        [Fact]
        public void App_selects_a_different_realm_variant_than_the_realm_active()
        {
            // Realm library has two variants; realm activates "r", the App picks
            // "x" (also a realm variant) — Apps select from the realm library.
            var realm = RealmWithSlot("login", new RealmPageSlot
            {
                Variants =
                [
                    new PageVariant { Id = "r", Name = "R", Schema = "realm-login" },
                    new PageVariant { Id = "x", Name = "X", Schema = "other-login" },
                ],
                ActiveVariantId = "r",
            });
            var app = new ApplicationSettings
            {
                PageSlots = new Dictionary<string, AppPageSlot>
                {
                    ["login"] = new AppPageSlot { InheritActive = false, ActiveVariantId = "x" },
                },
            };

            var eff = EffectiveSettings.Merge(realm, app);

            Assert.Equal("other-login", eff.Pages!["login"]);
        }

        [Fact]
        public void App_selecting_an_unknown_variant_falls_back_to_builtin()
        {
            var realm = RealmWithSlot("login", new RealmPageSlot
            {
                Variants = [new PageVariant { Id = "r", Name = "R", Schema = "realm-login" }],
                ActiveVariantId = "r",
            });
            var app = new ApplicationSettings
            {
                PageSlots = new Dictionary<string, AppPageSlot>
                {
                    ["login"] = new AppPageSlot { InheritActive = false, ActiveVariantId = "ghost" },
                },
            };

            var eff = EffectiveSettings.Merge(realm, app);

            Assert.True(eff.Pages is null || !eff.Pages.ContainsKey("login"));
        }

        [Fact]
        public void App_override_builtin_removes_the_inherited_slot()
        {
            var realm = RealmWithSlot("login", new RealmPageSlot
            {
                Variants = [new PageVariant { Id = "r", Name = "R", Schema = "realm-login" }],
                ActiveVariantId = "r",
            });
            var app = new ApplicationSettings
            {
                PageSlots = new Dictionary<string, AppPageSlot>
                {
                    // Override to built-in: not inheriting, no active variant.
                    ["login"] = new AppPageSlot { InheritActive = false, ActiveVariantId = null },
                },
            };

            var eff = EffectiveSettings.Merge(realm, app);

            Assert.True(eff.Pages is null || !eff.Pages.ContainsKey("login"));
        }

        [Fact]
        public void Realm_migration_converts_legacy_pages_to_active_variant()
        {
            var realm = new RealmSettingsDoc
            {
                Pages = new Dictionary<string, string> { ["login"] = "legacy-login" },
            };

            var changed = realm.MigratePagesToSlots();

            Assert.True(changed);
            Assert.Null(realm.Pages);
            var slot = realm.PageSlots!["login"];
            Assert.Single(slot.Variants);
            Assert.Equal("legacy-login", slot.Variants[0].Schema);
            Assert.Equal(slot.Variants[0].Id, slot.ActiveVariantId); // migrated as active
            Assert.Equal("legacy-login", EffectiveSettings.From(realm).Pages!["login"]);
        }

        [Fact]
        public void App_migration_drops_legacy_authored_schema_and_inherits()
        {
            // Apps no longer author their own schemas (the library is realm-global),
            // so a legacy App page override cannot be represented — migration drops
            // it and the slot inherits the realm.
            var realm = RealmWithSlot("login", new RealmPageSlot
            {
                Variants = [new PageVariant { Id = "r", Name = "R", Schema = "realm-login" }],
                ActiveVariantId = "r",
            });
            var app = new ApplicationSettings
            {
                Pages = new Dictionary<string, string> { ["login"] = "legacy-app-login" },
            };

            var changed = app.MigratePagesToSlots();

            Assert.True(changed);
            Assert.Null(app.Pages);
            Assert.True(app.PageSlots is null || !app.PageSlots.ContainsKey("login"));
            // Inherits the realm active variant.
            Assert.Equal("realm-login", EffectiveSettings.Merge(realm, app).Pages!["login"]);
        }
    }

    public class ClientSessionMerge
    {
        [Fact]
        public void App_values_override_individual_realm_fields()
        {
            var realm = Realm();
            var app = new ApplicationSettings
            {
                ClientSessions = new ApplicationClientSessionOverrides
                {
                    AbsoluteLifetime = TimeSpan.FromDays(3650),
                },
            };

            var effective = EffectiveSettings.Merge(realm, app);

            Assert.Equal(TimeSpan.FromDays(30), effective.ClientSessions!.IdleLifetime);
            Assert.Equal(TimeSpan.FromDays(3650), effective.ClientSessions.AbsoluteLifetime);
        }

        [Fact]
        public void Empty_app_override_uses_domain_defaults_when_realm_is_unconfigured()
        {
            var effective = EffectiveSettings.Merge(
                new RealmSettingsDoc(),
                new ApplicationSettings
                {
                    ClientSessions = new ApplicationClientSessionOverrides(),
                });

            Assert.Equal(ClientSessionPolicy.Defaults, effective.ClientSessions);
        }
    }
}
