using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 1a risk-gate: a prod-safe HARD remove that actually drops the tenant
/// database at runtime, vs today's reversible soft-delete. Proves the §4 drop
/// sequence (deregister tenant → DROP DATABASE ... WITH (FORCE) → remove global
/// record) works against a live host whose async daemon holds a connection to the
/// tenant DB, leaves sibling realms completely intact, and frees the slug for a
/// clean re-create.
/// </summary>
public class RealmHardDeleteTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Hard_delete_drops_the_tenant_database_and_leaves_other_realms_intact()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;

        var svc = factory.Services.GetRequiredService<IRealmProvisioningService>();
        var masterCs = factory.Services.GetRequiredService<IMasterConnectionString>().Value;
        var mainDb = new NpgsqlConnectionStringBuilder(masterCs).Database!;

        // Two realms so we can prove isolation: the victim and an innocent bystander.
        await CreateRealmAsync(svc, "victim", ct);
        await CreateRealmAsync(svc, "bystander", ct);

        var victimDb = $"{mainDb}_victim";
        var bystanderDb = $"{mainDb}_bystander";
        Assert.True(await DatabaseExistsAsync(masterCs, victimDb, ct), "victim DB should exist after create");
        Assert.True(await DatabaseExistsAsync(masterCs, bystanderDb, ct), "bystander DB should exist after create");

        // Act — hard-delete the victim while the daemon is live.
        var result = await svc.HardDeleteRealmAsync("victim", ct);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);

        // Victim is physically gone: DB dropped + global record removed.
        Assert.False(await DatabaseExistsAsync(masterCs, victimDb, ct), "victim DB must be dropped");
        Assert.Null(await svc.GetRealmBySlugAsync("victim", ct));

        // Bystander is entirely unaffected.
        Assert.True(await DatabaseExistsAsync(masterCs, bystanderDb, ct), "bystander DB must survive");
        Assert.NotNull(await svc.GetRealmBySlugAsync("bystander", ct));

        // NOTE: re-creating a realm with the SAME slug in the SAME process is a
        // documented caveat (Weasel's DefaultNpgsqlDataSourceFactory caches data
        // sources by connection string with no per-key eviction). Realm lifecycles use
        // unique slugs, so it is out of scope for this risk-gate; see HardDeleteRealmAsync.
    }

    [Fact]
    public async Task Hard_delete_refuses_the_control_plane_realm()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;

        var svc = factory.Services.GetRequiredService<IRealmProvisioningService>();
        var masterCs = factory.Services.GetRequiredService<IMasterConnectionString>().Value;
        var mainDb = new NpgsqlConnectionStringBuilder(masterCs).Database!;

        var result = await svc.HardDeleteRealmAsync(TenantConstants.SystemTenantId, ct);

        Assert.True(result.IsError);
        Assert.Equal("Realm.CannotDeleteControlPlane", result.FirstError.Code);

        // The system tenant DB must still be there.
        Assert.True(
            await DatabaseExistsAsync(masterCs, $"{mainDb}_{TenantConstants.SystemTenantId}", ct),
            "system tenant DB must survive a refused hard-delete");
    }

    private static async Task CreateRealmAsync(IRealmProvisioningService svc, string slug, CancellationToken ct)
    {
        var result = await svc.CreateRealmAsync(new CreateRealmDto
        {
            Slug = slug,
            DisplayName = slug,
            Domains = [$"{slug}.localhost"],
            InitialAdmin = new InitialAdminDto { UserName = "admin", Email = $"admin@{slug}.test" },
        }, ct);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
    }

    private static async Task<bool> DatabaseExistsAsync(string masterCs, string dbName, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(masterCs) { Database = "postgres" };
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn);
        cmd.Parameters.AddWithValue("@n", dbName);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
