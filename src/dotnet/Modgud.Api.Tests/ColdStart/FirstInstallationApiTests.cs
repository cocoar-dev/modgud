using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Pins the operator/CI contract: the shell issues the bearer token and the
/// browser or automation completes installation through the same HTTP API.
/// </summary>
public class FirstInstallationApiTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    /// <summary>IC-CO-01 — /health runs in the realm-independent terminal branch, so the
    /// drain short-circuit registered later never saw it: during a drain readiness went
    /// through the health-check pipeline (logged at Error) instead of a plain 503.</summary>
    [Fact]
    public async Task Readiness_answers_the_drain_503_while_the_node_is_stopping()
    {
        await using var host = await Fixture.CreateUninitializedHostAsync();
        using var client = host.Factory.CreateClient();
        host.Services.GetRequiredService<Modgud.Api.Cluster.ShutdownState>().MarkStopping();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // The short-circuit answers with the cluster entry only; the health-check
        // pipeline (whose cluster check says the same as a backstop) runs every check.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var check = Assert.Single(body.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal("cluster", check.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Recovery_token_can_complete_zero_realm_installation_over_http()
    {
        await using var host = await Fixture.CreateUninitializedHostAsync();
        using var client = host.Factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var before = await client.GetFromJsonAsync<InstallationStatusResponse>(
            "/api/install/status", ct);
        Assert.NotNull(before);
        Assert.False(before.IsInitialized);
        Assert.False(before.HasRealms);

        var cli = await CliHarness.RunAsync(
            host.Services,
            "install-link",
            "--base-url", "http://localhost",
            "--minutes", "10",
            "--json");
        Assert.Equal(0, cli.ExitCode);
        Assert.Equal("", cli.StdErr);

        using var issuedJson = JsonDocument.Parse(cli.StdOut.Trim());
        var token = issuedJson.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var complete = await client.PostAsJsonAsync(
            "/api/install/complete",
            new
            {
                Token = token,
                Realm = new
                {
                    Slug = "first",
                    DisplayName = "First Realm",
                    Description = "CI installation test",
                    Domains = new[] { "localhost" },
                    PrimaryDomain = "localhost",
                },
                Admin = new
                {
                    UserName = "first-admin",
                    Email = "first-admin@localhost",
                    Firstname = "First",
                    Lastname = "Admin",
                    Password = "TestPass1234",
                },
            },
            ct);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var after = await client.GetFromJsonAsync<InstallationStatusResponse>(
            "/api/install/status", ct);
        Assert.NotNull(after);
        Assert.True(after.IsInitialized);
        Assert.True(after.HasRealms);
        Assert.Equal("first", after.RealmSlug);

        var realms = host.Services.GetRequiredService<IRealmProvisioningService>();
        var first = await realms.GetRealmBySlugAsync("first", ct);
        Assert.NotNull(first);
        Assert.True(first.IsActive);
        Assert.True(first.IsControlPlane);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession("first");
        var adminGroup = await session.Query<Group>()
            .Where(g => g.Name == "Administrators")
            .FirstAsync(ct);
        var adminRole = await session.LoadAsync<PermissionRole>(
            Assert.Single(adminGroup.RoleIds), ct);
        Assert.NotNull(adminRole);
        Assert.True(adminRole.IsRealmAdmin);
        Assert.Single(adminGroup.MemberIds);

        var login = await client.PostAsJsonAsync(
            "/api/account/login",
            new { UserName = "first-admin", Password = "TestPass1234" },
            ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var replay = await client.PostAsJsonAsync(
            "/api/install/complete",
            new
            {
                Token = token,
                Realm = new
                {
                    Slug = "other",
                    DisplayName = "Other",
                    Domains = new[] { "other.localhost" },
                    PrimaryDomain = "other.localhost",
                },
                Admin = new
                {
                    UserName = "other-admin",
                    Email = "other-admin@localhost",
                    Password = "TestPass1234",
                },
            },
            ct);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    /// <summary>
    /// F.inst-I1 — a failure after the initial realm document is committed but before the
    /// realm is seeded must not strand the deployment: the half-created realm is rolled
    /// back, and the operator can retry with the same link.
    /// </summary>
    [Fact]
    public async Task A_seeding_failure_during_installation_leaves_a_retryable_deployment()
    {
        await using var host = await Fixture.CreateUninitializedHostAsync();
        using var client = host.Factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var cli = await CliHarness.RunAsync(
            host.Services, "install-link", "--base-url", "http://localhost", "--minutes", "10", "--json");
        Assert.Equal(0, cli.ExitCode);
        using var issuedJson = JsonDocument.Parse(cli.StdOut.Trim());
        var token = issuedJson.RootElement.GetProperty("token").GetString();

        object Payload() => new
        {
            Token = token,
            Realm = new
            {
                Slug = "first",
                DisplayName = "First Realm",
                Domains = new[] { "localhost" },
                PrimaryDomain = "localhost",
            },
            Admin = new
            {
                UserName = "first-admin",
                Email = "first-admin@localhost",
                Password = "TestPass1234",
            },
        };

        host.Factory.SeedingFault.ArmOnce();
        var failed = await client.PostAsJsonAsync("/api/install/complete", Payload(), ct);
        Assert.False(failed.IsSuccessStatusCode);

        var realms = host.Services.GetRequiredService<IRealmProvisioningService>();
        Assert.Null(await realms.GetRealmBySlugAsync("first", ct));

        var retry = await client.PostAsJsonAsync("/api/install/complete", Payload(), ct);
        Assert.True(retry.StatusCode == HttpStatusCode.OK,
            $"retry {(int)retry.StatusCode}: {await retry.Content.ReadAsStringAsync(ct)}");

        var status = await client.GetFromJsonAsync<InstallationStatusResponse>("/api/install/status", ct);
        Assert.NotNull(status);
        Assert.True(status.IsInitialized);
    }

    /// <summary>
    /// F.inst-I1 (crash case) — a process that died right after the initial realm document
    /// was committed leaves an inactive realm shell without users. A new installation link
    /// and a new installation must still work: the shell is cleaned up, not a dead end.
    /// </summary>
    [Fact]
    public async Task An_abandoned_initial_realm_shell_does_not_block_a_new_installation()
    {
        await using var host = await Fixture.CreateUninitializedHostAsync();
        using var client = host.Factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // The state a crash between "realm document committed" and "admin created" leaves.
        var realms = host.Services.GetRequiredService<IRealmProvisioningService>();
        var shell = await realms.CreateInitialRealmAsync(new Modgud.Application.DTOs.Realms.CreateRealmDto
        {
            Slug = "first", DisplayName = "First Realm", Domains = ["localhost"], PrimaryDomain = "localhost",
        }, ct);
        Assert.False(shell.IsError, shell.IsError ? shell.FirstError.Description : string.Empty);

        var cli = await CliHarness.RunAsync(host.Services,
            "install-link", "--base-url", "http://localhost", "--minutes", "10", "--json");
        Assert.True(cli.ExitCode == 0, $"install-link refused: {cli.StdErr}");
        using var issuedJson = JsonDocument.Parse(cli.StdOut.Trim());
        var token = issuedJson.RootElement.GetProperty("token").GetString();

        var complete = await client.PostAsJsonAsync("/api/install/complete", new
        {
            Token = token,
            Realm = new { Slug = "first", DisplayName = "First Realm", Domains = new[] { "localhost" }, PrimaryDomain = "localhost" },
            Admin = new { UserName = "first-admin", Email = "first-admin@localhost", Password = "TestPass1234" },
        }, ct);
        Assert.True(complete.StatusCode == HttpStatusCode.OK,
            $"complete {(int)complete.StatusCode}: {await complete.Content.ReadAsStringAsync(ct)}");

        var first = await realms.GetRealmBySlugAsync("first", ct);
        Assert.NotNull(first);
        Assert.True(first.IsActive);
    }

    [Fact]
    public async Task Installation_sends_the_operator_back_to_the_exact_origin_of_the_install_link()
    {
        await using var host = await Fixture.CreateUninitializedHostAsync();
        using var client = host.Factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // A deployment reached on a non-default port: the install link carries it,
        // so the post-install redirect must carry it too — anything derived from the
        // bare PrimaryDomain would strand the operator on a dead URL.
        const string baseUrl = "http://localhost:18081";
        var cli = await CliHarness.RunAsync(
            host.Services, "install-link", "--base-url", baseUrl, "--minutes", "10", "--json");
        Assert.Equal(0, cli.ExitCode);

        using var issuedJson = JsonDocument.Parse(cli.StdOut.Trim());
        var token = issuedJson.RootElement.GetProperty("token").GetString();
        // The install URL itself is the base URL verbatim — the redirect below is its mirror.
        Assert.StartsWith($"{baseUrl}/install?token=",
            issuedJson.RootElement.GetProperty("installUrl").GetString());

        var complete = await client.PostAsJsonAsync(
            "/api/install/complete",
            new
            {
                Token = token,
                Realm = new
                {
                    Slug = "ported",
                    DisplayName = "Ported",
                    Domains = new[] { "auth.localhost" },
                    // Deliberately DIFFERENT from the install host: the realm's configured
                    // primary domain governs every outbound link from here on, but it must
                    // not hijack the redirect of the operator who is standing on :18081.
                    PrimaryDomain = "auth.localhost",
                },
                Admin = new
                {
                    UserName = "ported-admin",
                    Email = "ported-admin@localhost",
                    Password = "TestPass1234",
                },
            },
            ct);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var body = await complete.Content.ReadFromJsonAsync<CompleteInstallationResult>(ct);
        Assert.NotNull(body);
        Assert.Equal($"{baseUrl}/login", body.LoginUrl);
        Assert.Equal("auth.localhost", body.PrimaryDomain);

        // ...and the SAME origin becomes the realm's declared public origin, so every
        // outbound link from here on resolves to where the deployment actually is —
        // no environment guessing, no dropped port.
        var realms = host.Services.GetRequiredService<IRealmProvisioningService>();
        var realm = await realms.GetRealmBySlugAsync("ported", ct);
        Assert.NotNull(realm);
        Assert.Equal(baseUrl, realm.PublicBaseUrl);
        Assert.Equal(baseUrl, RealmPublicOrigin.Resolve(realm));
        // The primary domain stays a bare host — it is the WebAuthn RP ID.
        Assert.Equal("auth.localhost", realm.PrimaryDomain);
    }

    private sealed record InstallationStatusResponse(
        bool IsInitialized,
        bool HasRealms,
        string? RealmSlug,
        DateTimeOffset? CompletedAt);

    private sealed record CompleteInstallationResult(
        string RealmSlug,
        string PrimaryDomain,
        string LoginUrl);
}
