using Modgud.Infrastructure.Realms;

namespace Modgud.Infrastructure.Scheduling;

/// <summary>
/// B9-JS-02 — which realm a scheduled run executes in. A realm job runs in the realm it was
/// scheduled for. A system job stored the control plane of scheduling time; after a
/// control-plane transfer a run already in flight would still have written there, so it
/// runs in the control plane current at run time (the stored slug when none is unique).
/// </summary>
public static class JobRunTenant
{
    public static async Task<string> ResolveAsync(string storedSlug, JobScope scope, IRealmCache realms)
    {
        if (scope != JobScope.System) return storedSlug;
        var controlPlanes = (await realms.GetAllActiveAsync()).Where(r => r.IsControlPlane).ToList();
        return controlPlanes.Count == 1 ? controlPlanes[0].Slug : storedSlug;
    }
}
