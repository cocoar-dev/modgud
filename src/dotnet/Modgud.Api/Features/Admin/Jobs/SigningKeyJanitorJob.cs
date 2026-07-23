using Quartz;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// Daily sweep that hard-deletes per-realm signing keys whose rotation overlap
/// window has elapsed (<c>RetiredAt + RealmKeyStore.RotationOverlap &lt; now</c>).
///
/// <para>Retired keys are kept — and listed in the realm's JWKS — for the
/// overlap window so tokens issued just before a <see cref="IRealmKeyStore.RotateAsync"/>
/// stay validatable. Once the window passes a retired key must stop being
/// trusted; the verification cache evicts it on its own (the cached set carries
/// a <c>ValidUntil</c>), and this janitor removes the now-dead row from the
/// tenant DB so retired private material doesn't accumulate.</para>
///
/// <para>Per-realm and idempotent: Quartz creates one instance per realm, and a
/// realm with no expired retired keys ends after a single indexed query. Its
/// schedule deliberately remains active for deactivated realms because their
/// soft-deleted tenant databases still contain private key material.</para>
/// </summary>
[DisallowConcurrentExecution]
public class SigningKeyJanitorJob(
    IRealmKeyStore keyStore,
    ISecurityAuditLog securityAudit) : IJob
{
    public const string Key = "signing-key-janitor";
    public const string Name = "Signing Key Janitor";
    public const string Description =
        "Hard-deletes per-realm signing keys whose rotation overlap window " +
        "(RetiredAt + 30 days) has elapsed. Active and still-in-overlap keys " +
        "are untouched. Per-realm, idempotent.";

    /// <summary>05:00 UTC daily — after the DCR GC + history-retention sweeps.</summary>
    public const string DefaultCron = "0 0 5 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var realmSlug = TenantContext.Current;
        var purged = await keyStore.PurgeExpiredRetiredKeysAsync(realmSlug, ct);

        if (purged > 0)
        {
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.SigningKeyPurged,
                Realm = realmSlug,
                Level = "Info",
                Status = "purged",
                Reason = $"purged {purged} expired retired key(s)",
                Message = $"signing-key janitor purged {purged} expired retired key(s)",
            });
        }

        context.Result = purged == 0
            ? "No expired signing keys"
            : $"Purged {purged} expired signing key(s)";
    }
}
