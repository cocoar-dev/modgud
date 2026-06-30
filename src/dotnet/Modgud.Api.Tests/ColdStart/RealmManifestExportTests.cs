using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 1c (Export): the structure-only export round-trips with apply. Imports a realm
/// with a passwordless user + a confidential client, exports it, asserts no secrets /
/// passwords / seeded entities leak, re-applies the unedited export idempotently, then edits
/// the export to set the user's password and re-applies — proving the
/// export → edit → "set a password" → apply flow.
/// </summary>
public class RealmManifestExportTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Export_is_structure_only_and_round_trips_with_apply_and_password_set()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();

        const string slug = "exporttest";
        var manifest = new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = slug,
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = $"admin@{slug}.test" },
            },
            Apps =
            [
                new RealmManifestApp { Slug = "ex-app", DisplayName = "Ex App",
                    Permissions = [new RealmManifestPermission("ex", "read")] },
            ],
            Clients =
            [
                new RealmManifestClient
                {
                    ClientId = "ex-web",
                    ClientType = "confidential",
                    RedirectUris = ["https://ex.test/cb"],
                    Scopes = ["openid"],
                    AllowedGrantTypes = ["authorization_code", "refresh_token"],
                    Apps = ["ex-app"],
                },
            ],
            Users = [new RealmManifestUser { Key = "bob", Email = "bob@ex.test", UserName = "bob" }], // passwordless
        };
        Assert.False((await applier.ImportNewRealmAsync(manifest, ct)).IsError);

        // ── Export ────────────────────────────────────────────────────────────
        var exported = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(exported.IsError, exported.IsError ? exported.FirstError.Description : string.Empty);
        var m = exported.Value;

        // Structure-only: no client secret, no user password.
        var exClient = Assert.Single(m.Clients, c => c.ClientId == "ex-web");
        Assert.Null(exClient.ClientSecret);
        Assert.Contains("openid", exClient.Scopes);
        Assert.Contains("ex-app", exClient.Apps);
        var exUser = Assert.Single(m.Users, u => u.UserName == "bob");
        Assert.Null(exUser.Password);

        // Seeded entities that can't cleanly re-apply are excluded; the authored app survives.
        Assert.Contains(m.Apps, a => a.Slug == "ex-app");
        Assert.DoesNotContain(m.Apps, a => a.Slug == "modgud");     // system app
        Assert.DoesNotContain(m.Scopes, s => s.Name == "openid");   // standard scope

        // Settings ARE exported (all sections, current values) so you can see what to change.
        Assert.NotNull(m.Settings);
        Assert.Equal("Optional", m.Settings!.RegistrationFields!.Username); // shipped default
        Assert.Null(m.Settings.SelfRegistration!.CaptchaSecret);            // write-only — never exported

        // ── Re-apply the UNEDITED export = idempotent ──────────────────────────
        Assert.False((await applier.UpdateRealmAsync(m, ct)).IsError);

        // ── Edit a setting and re-apply → it round-trips ───────────────────────
        var withSetting = m with
        {
            Settings = new UpdateRealmSettingsDto
            {
                RegistrationFields = new UpdateRegistrationFieldsSettingsDto { Username = "Required" },
            },
        };
        Assert.False((await applier.UpdateRealmAsync(withSetting, ct)).IsError);
        var reexport = await exporter.ExportRealmAsync(slug, ct);
        Assert.Equal("Required", reexport.Value.Settings!.RegistrationFields!.Username);

        // ── Edit: set bob's password, re-apply ─────────────────────────────────
        var withPassword = m with
        {
            Users = m.Users.Select(u => u.UserName == "bob" ? u with { Password = "Bobsecret1!" } : u).ToList(),
        };
        Assert.False((await applier.UpdateRealmAsync(withPassword, ct)).IsError);

        await InTenantAsync(factory, slug, async sp =>
        {
            var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var bob = await userManager.FindByNameAsync("bob");
            Assert.NotNull(bob);
            Assert.True(await userManager.HasPasswordAsync(bob!), "bob should have a password after apply");
        });
    }

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
