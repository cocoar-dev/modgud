using Modgud.Authentication.Gdpr;
using Modgud.Authentication.SelfRegistration;
using Modgud.Infrastructure.Audit;
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
/// <para>Quartz creates one instance per realm. The scheduler enters that
/// realm's <see cref="TenantContext"/> before resolving this job, so all
/// constructor-injected services bind to exactly one tenant database.</para>
/// </summary>
[DisallowConcurrentExecution]
public class AccountLifecycleSweepJob(
    IGdprService gdpr,
    IRegistrationInviteService inviteService,
    ISecurityAuditLog securityAudit) : IJob
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
        var realmSlug = TenantContext.Current;

        var (reminded, erased) = await gdpr.RunSelfServiceSweepAsync(ct);
        var purged = await gdpr.RunAdminRetentionPurgeAsync(ct);

        // ADR-0012 §8 — prune used/expired invite codes (hygiene only).
        var inviteCodesPruned = await inviteService.PruneAsync(ct);

        if (reminded + erased + purged + inviteCodesPruned > 0)
        {
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.AccountLifecycleSwept,
                Level = "Info",
                Realm = realmSlug,
                Status = "swept",
                Reason = $"reminded={reminded} selfErased={erased} autoPurged={purged} inviteCodesPruned={inviteCodesPruned}",
                Message = $"Account-lifecycle sweep — Realm={realmSlug} Reminded={reminded} SelfErased={erased} AutoPurged={purged} InviteCodesPruned={inviteCodesPruned}",
            });
        }

        context.Result =
            $"{reminded} reminded, {erased} self-erased, {purged} auto-purged, {inviteCodesPruned} invite-codes pruned";
    }
}
