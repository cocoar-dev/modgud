using Modgud.Application.Scheduling;
using Modgud.Infrastructure.Realms;
using Modgud.Infrastructure.Scheduling;

namespace Modgud.Tests.Unit.Infrastructure;

/// <summary>
/// B9-JS-02 — a system job stored the control-plane slug when it was scheduled and ran
/// there, so a run in flight after a control-plane transfer wrote to the old realm. A
/// system job runs in the control plane current at run time; a realm job in its own realm.
/// </summary>
public class SystemJobTenantTests
{
    [Fact]
    public async Task A_system_job_runs_in_the_current_control_plane()
    {
        var cache = new Cache(new TenantInfo("old-cp", IsControlPlane: false, IsActive: true),
                              new TenantInfo("new-cp", IsControlPlane: true, IsActive: true));

        Assert.Equal("new-cp", await JobRunTenant.ResolveAsync("old-cp", JobScope.System, cache));
    }

    [Fact]
    public async Task A_realm_job_keeps_its_realm()
    {
        var cache = new Cache(new TenantInfo("acme", false, true), new TenantInfo("cp", true, true));

        Assert.Equal("acme", await JobRunTenant.ResolveAsync("acme", JobScope.Realm, cache));
    }

    [Fact]
    public async Task Without_a_single_control_plane_the_stored_slug_stays()
    {
        Assert.Equal("cp", await JobRunTenant.ResolveAsync("cp", JobScope.System, new Cache()));
    }

    private sealed class Cache(params TenantInfo[] realms) : IRealmCache
    {
        public Task<TenantInfo?> ResolveDomainAsync(string hostname) => Task.FromResult<TenantInfo?>(null);
        public Task<RealmResolution?> ResolveAsync(string hostname) => Task.FromResult<RealmResolution?>(null);
        public Task<IReadOnlyList<TenantInfo>> GetAllActiveAsync() => Task.FromResult<IReadOnlyList<TenantInfo>>(realms);
        public void Invalidate() { }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
