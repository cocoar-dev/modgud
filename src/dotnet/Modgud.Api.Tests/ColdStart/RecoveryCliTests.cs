using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 1 (the CLI — the operator's first tool): the Recovery CLI had zero
/// automated coverage. These tests drive it in-process via <see cref="CliHarness"/>
/// against a cold-booted host and assert stdout, exit code, and the real
/// resulting documents — covering the operator's first journey (bootstrap-admin)
/// and the realm-resolution finding (a misspelled / silently-defaulted --realm).
/// </summary>
public class RecoveryCliTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    // ── bootstrap-admin — the operator's actual first tool ───────────────

    [Fact]
    public async Task Bootstrap_admin_direct_creates_a_login_capable_admin()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        var result = await CliHarness.RunAsync(host.Services,
            "bootstrap-admin", "--username", "cliadmin", "--email", "cliadmin@cli.local",
            "--password", "TestPass1234");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Admin created", result.StdOut);

        // The human path: the just-created admin can actually sign in.
        var client = host.Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = "cliadmin", Password = "TestPass1234" }, ct);

        Assert.True(login.IsSuccessStatusCode,
            $"login after bootstrap-admin failed: {login.StatusCode} — {await login.Content.ReadAsStringAsync(ct)}");
    }

    [Fact]
    public async Task Bootstrap_admin_without_password_issues_an_invite_and_prints_a_link()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        var result = await CliHarness.RunAsync(host.Services,
            "bootstrap-admin", "--username", "inviteadmin", "--email", "invite@cli.local");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Bootstrap-invite issued", result.StdOut);
        Assert.Contains("/bootstrap?token=", result.StdOut);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession("system");
        var invite = await session.Query<PendingAdminInvite>().SingleAsync(ct);
        Assert.Equal("inviteadmin", invite.UserName);
        Assert.Equal("invite@cli.local", invite.Email);
    }

    [Fact]
    public async Task Bootstrap_admin_without_email_fails_with_usage_and_nonzero_exit()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();

        var result = await CliHarness.RunAsync(host.Services,
            "bootstrap-admin", "--password", "TestPass1234");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Usage: recover bootstrap-admin", result.StdErr);
    }

    // ── --realm resolution (the silent-tenant finding) ───────────────────

    [Fact]
    public async Task Tenant_scoped_command_with_unknown_realm_fails_with_a_clear_message()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();

        var result = await CliHarness.RunAsync(host.Services, "list", "--realm", "does-not-exist");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Realm 'does-not-exist' not found", result.StdErr);
    }

    [Fact]
    public async Task Tenant_scoped_command_announces_the_implicit_default_when_multiple_realms_exist()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        // A second active realm makes the implicit 'system' default ambiguous.
        var globalStore = host.Services.GetRequiredService<IGlobalStore>();
        await using (var gs = globalStore.LightweightSession())
        {
            gs.Store(new Realm
            {
                Id = Guid.NewGuid(),
                Slug = "second",
                DisplayName = "Second",
                Domains = ["second.localhost"],
                PrimaryDomain = "second.localhost",
                IsControlPlane = false,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await gs.SaveChangesAsync(ct);
        }

        var result = await CliHarness.RunAsync(host.Services, "list"); // no --realm

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("no --realm specified", result.StdErr);
        Assert.Contains("'system'", result.StdErr);
    }

    // ── realm-domain guards (previously never validated) ─────────────────

    [Fact]
    public async Task Realm_domain_commands_validate_and_apply()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();

        // set-primary to a domain not in the realm → clear error, no change.
        var guard = await CliHarness.RunAsync(host.Services,
            "realm-set-primary-domain", "--slug", "system", "--domain", "not-a-domain.example");
        Assert.Equal(1, guard.ExitCode);
        Assert.Contains("not in realm", guard.StdErr);

        // add-domain happy path.
        var add = await CliHarness.RunAsync(host.Services,
            "realm-add-domain", "--slug", "system", "--domain", "cli-added.example");
        Assert.Equal(0, add.ExitCode);
        Assert.Contains("Added 'cli-added.example'", add.StdOut);

        // now set-primary to the just-added domain → succeeds.
        var setPrimary = await CliHarness.RunAsync(host.Services,
            "realm-set-primary-domain", "--slug", "system", "--domain", "cli-added.example");
        Assert.Equal(0, setPrimary.ExitCode);
        Assert.Contains("Set PrimaryDomain", setPrimary.StdOut);
    }

    // ── dispatch basics ──────────────────────────────────────────────────

    [Fact]
    public async Task Help_no_args_and_unknown_command_have_documented_exit_codes()
    {
        var help = await CliHarness.RunAsync(Factory.Services, "help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("Modgud Recovery CLI", help.StdOut);

        var noArgs = await CliHarness.RunAsync(Factory.Services);
        Assert.Equal(1, noArgs.ExitCode);

        var unknown = await CliHarness.RunAsync(Factory.Services, "frobnicate");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("Unknown command", unknown.StdErr);
    }
}
