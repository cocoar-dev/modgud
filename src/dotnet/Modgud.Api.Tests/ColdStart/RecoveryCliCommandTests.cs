using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 1 per-command coverage for the rest of the Recovery CLI (the commands
/// not exercised by <see cref="RecoveryCliTests"/>). Each drives the real CLI via
/// <see cref="CliHarness"/> and asserts stdout, exit code, and — where it mutates
/// — the resulting document. Read-only / user-scoped commands run on the shared
/// cold-boot host with unique names; realm-mutating commands use a throwaway
/// isolated host so they can't disturb the shared boot state.
/// </summary>
public class RecoveryCliCommandTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    private async Task<UserView> ArrangeUserAsync(string acronym, string email)
        => await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Cli", lastname: "User", acronym: acronym, email: email,
            password: "TestPass1234", isRealmAdmin: false);

    // ── list ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_prints_users_with_a_header()
    {
        await ArrangeUserAsync("listu", "listu@cli.local");

        var result = await CliHarness.RunAsync(Factory.Services, "list");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("UserName", result.StdOut);
        Assert.Contains("listu", result.StdOut);
    }

    // ── reset-2fa ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_2fa_clears_email_otp_and_reports_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await ArrangeUserAsync("reset2fa", "reset2fa@cli.local");

        // Arrange a real 2FA flag to clear.
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        await using (var s = store.LightweightSession("system"))
        {
            var u = await s.LoadAsync<ApplicationUser>(user.Id, ct);
            u!.EmailOtpEnabled = true;
            s.Store(u);
            await s.SaveChangesAsync(ct);
        }

        var result = await CliHarness.RunAsync(Factory.Services, "reset-2fa", "reset2fa");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("2FA reset for", result.StdOut);

        await using var verify = store.QuerySession("system");
        var after = await verify.LoadAsync<ApplicationUser>(user.Id, ct);
        Assert.False(after!.EmailOtpEnabled);
    }

    [Fact]
    public async Task Reset_2fa_with_unknown_user_and_missing_arg_fail_loudly()
    {
        var missing = await CliHarness.RunAsync(Factory.Services, "reset-2fa");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("Usage: recover reset-2fa", missing.StdErr);

        var unknown = await CliHarness.RunAsync(Factory.Services, "reset-2fa", "ghost-user");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("User not found", unknown.StdErr);
    }

    // ── set-email ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Set_email_updates_the_address()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await ArrangeUserAsync("setmail", "setmail@cli.local");

        var result = await CliHarness.RunAsync(Factory.Services,
            "set-email", "setmail", "setmail-new@cli.local");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Email updated", result.StdOut);

        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        await using var verify = store.QuerySession("system");
        var after = await verify.LoadAsync<ApplicationUser>(user.Id, ct);
        Assert.Equal("setmail-new@cli.local", after!.Email);
    }

    [Fact]
    public async Task Set_email_rejects_an_invalid_address()
    {
        await ArrangeUserAsync("setbad", "setbad@cli.local");

        var result = await CliHarness.RunAsync(Factory.Services,
            "set-email", "setbad", "not-an-email");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Invalid email", result.StdErr);
    }

    // ── magic-link ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Magic_link_prints_a_one_time_login_url()
    {
        await ArrangeUserAsync("magicu", "magicu@cli.local");

        var result = await CliHarness.RunAsync(Factory.Services, "magic-link", "magicu");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/magic-login?userId=", result.StdOut);
    }

    // ── realm-list ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Realm_list_shows_the_system_realm()
    {
        var result = await CliHarness.RunAsync(Factory.Services, "realm-list");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("system", result.StdOut);
        Assert.Contains("[CP]", result.StdOut); // system is the control plane
    }

    // ── control-plane ──────────────────────────────────────────────────────

    [Fact]
    public async Task Control_plane_list_reports_the_system_realm()
    {
        var result = await CliHarness.RunAsync(Factory.Services, "control-plane", "list");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Control-plane realm: system", result.StdOut);
    }

    [Fact]
    public async Task Control_plane_transfer_to_unknown_realm_fails()
    {
        var result = await CliHarness.RunAsync(Factory.Services,
            "control-plane", "transfer", "does-not-exist");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not found", result.StdErr);
    }

    [Fact]
    public async Task Control_plane_transfer_moves_the_flag()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        // A second active realm to receive the control plane (the bare global
        // record is enough; the best-effort app-seed into its DB is allowed to
        // fail without failing the transfer).
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

        var result = await CliHarness.RunAsync(host.Services, "control-plane", "transfer", "second");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("transferred", result.StdOut);

        var svc = host.Services.GetRequiredService<IRealmProvisioningService>();
        var cp = await svc.GetControlPlaneRealmAsync(ct);
        Assert.Equal("second", cp!.Slug);
    }

    // ── realm-remove-domain ────────────────────────────────────────────────

    [Fact]
    public async Task Realm_remove_domain_applies_and_guards()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();

        // Add a removable domain, then remove it.
        var add = await CliHarness.RunAsync(host.Services,
            "realm-add-domain", "--slug", "system", "--domain", "removable.example");
        Assert.Equal(0, add.ExitCode);

        var remove = await CliHarness.RunAsync(host.Services,
            "realm-remove-domain", "--slug", "system", "--domain", "removable.example");
        Assert.Equal(0, remove.ExitCode);
        Assert.Contains("Removed 'removable.example'", remove.StdOut);

        // Guard: cannot remove the PrimaryDomain.
        var guard = await CliHarness.RunAsync(host.Services,
            "realm-remove-domain", "--slug", "system", "--domain", "localhost");
        Assert.Equal(1, guard.ExitCode);
        Assert.Contains("PrimaryDomain", guard.StdErr);
    }

    // ── rotate-signing-key ─────────────────────────────────────────────────

    [Fact]
    public async Task Rotate_signing_key_reports_a_new_kid()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();

        var result = await CliHarness.RunAsync(host.Services, "rotate-signing-key");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("new active kid", result.StdOut);
    }

    // ── migrate-cc-credentials ─────────────────────────────────────────────

    [Fact]
    public async Task Migrate_cc_credentials_is_a_clean_no_op_when_nothing_to_migrate()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();

        var result = await CliHarness.RunAsync(host.Services, "migrate-cc-credentials");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No client_credentials clients need migration", result.StdOut);
    }

    // ── adopt-tenant ───────────────────────────────────────────────────────

    [Fact]
    public async Task Adopt_tenant_fails_loudly_on_missing_args_and_missing_database()
    {
        var missing = await CliHarness.RunAsync(Factory.Services, "adopt-tenant");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("Usage: recover adopt-tenant", missing.StdErr);

        var noDb = await CliHarness.RunAsync(Factory.Services,
            "adopt-tenant", "ghost-realm", "Ghost", "ghost.localhost");
        Assert.Equal(1, noDb.ExitCode);
        Assert.Contains("does not exist", noDb.StdErr);
    }

    // ── rebuild-projections ────────────────────────────────────────────────

    [Fact]
    public async Task Rebuild_projections_runs_and_reports_each_projection()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();

        var result = await CliHarness.RunAsync(host.Services, "rebuild-projections");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ViewProjections", result.StdOut);
    }
}
