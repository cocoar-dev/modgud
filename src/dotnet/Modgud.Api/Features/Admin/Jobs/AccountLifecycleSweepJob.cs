using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Gdpr;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// Daily per-realm sweep that drives the account-lifecycle deadlines
/// (Account-Lifecycle plan, WS3 + WS4):
/// <list type="bullet">
///   <item>sends the "about to be deleted" reminder for self-service requests
///   nearing their grace deadline (once per request);</item>
///   <item>permanently erases self-service requests whose grace deadline has
///   passed;</item>
///   <item>auto-purges admin recycle-bin users past their retention deadline
///   (only when the realm has AutoPurge enabled).</item>
/// </list>
///
/// <para>Mirrors <see cref="DcrGcJob"/>'s multi-tenant shape: it reads the
/// realm list from the master DB, then runs the per-realm work inside each
/// realm's <see cref="TenantContext"/> so the scoped <c>IGdprService</c> (and
/// its Marten session + RealmSettings) resolve against the right tenant DB —
/// there is no HttpContext in a scheduled job.</para>
/// </summary>
[DisallowConcurrentExecution]
public class AccountLifecycleSweepJob(
    IServiceScopeFactory scopeFactory,
    IDocumentStore store,
    ILogger<AccountLifecycleSweepJob> logger) : IJob
{
    public const string Key = "account-lifecycle-sweep";
    public const string Name = "Account Lifecycle Sweep";
    public const string Description =
        "Per-realm: sends self-service deletion reminders, erases grace-expired self-service " +
        "deletions, and auto-purges admin recycle-bin users past retention (when AutoPurge is on). " +
        "Deadlines/lead-times come from RealmSettings.Deletion. Idempotent.";

    /// <summary>03:30 UTC daily.</summary>
    public const string DefaultCron = "0 30 3 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        await using var masterSession = store.LightweightSession(TenantConstants.SystemTenantId);
        var realms = await masterSession.Query<Realm>()
            .Where(r => r.IsActive)
            .ToListAsync(ct);

        int realmsTouched = 0, totalReminded = 0, totalErased = 0, totalPurged = 0;
        foreach (var realm in realms)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var scope = scopeFactory.CreateScope();
                using (TenantContext.Enter(realm.Slug))
                {
                    // Resolve INSIDE the tenant context so the scoped GdprService's
                    // Marten session binds to this realm's DB.
                    var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
                    var (reminded, erased) = await gdpr.RunSelfServiceSweepAsync(ct);
                    var purged = await gdpr.RunAdminRetentionPurgeAsync(ct);

                    totalReminded += reminded;
                    totalErased += erased;
                    totalPurged += purged;
                    if (reminded + erased + purged > 0)
                        logger.LogInformation(
                            "Auth: Account-lifecycle sweep — Realm={Realm} Reminded={Reminded} SelfErased={Erased} AutoPurged={Purged}",
                            realm.Slug, reminded, erased, purged);
                }
                realmsTouched++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Auth: Account-lifecycle sweep failed for realm {Realm}", realm.Slug);
            }
        }

        context.Result =
            $"{realmsTouched} realm(s): {totalReminded} reminded, {totalErased} self-erased, {totalPurged} auto-purged";
    }
}
