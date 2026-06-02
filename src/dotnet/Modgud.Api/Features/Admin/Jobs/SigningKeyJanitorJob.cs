using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using Modgud.Domain.Realms;
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
/// <para>Per-realm and idempotent: a realm with no expired retired keys ends
/// after a single indexed query. Mirrors <see cref="DcrGcJob"/>: realms live in
/// the master DB; the control-plane realm uses the "system" tenant, tenant
/// realms use their slug.</para>
/// </summary>
[DisallowConcurrentExecution]
public class SigningKeyJanitorJob(
    IServiceScopeFactory scopeFactory,
    IRealmKeyStore keyStore,
    ILogger<SigningKeyJanitorJob> logger) : IJob
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

        using var rootScope = scopeFactory.CreateScope();
        var store = rootScope.ServiceProvider.GetRequiredService<IDocumentStore>();

        // Realms live in the master DB. The control-plane realm uses the
        // "system" tenant; tenant realms use their slug. NOTE: we deliberately
        // do NOT filter on IsActive — a deactivated realm is a soft-delete that
        // keeps its tenant DB, and its retired keys still hold private signing
        // material that must not accumulate indefinitely.
        await using var masterSession = store.LightweightSession("system");
        var realms = await masterSession.Query<Realm>()
            .ToListAsync(ct);

        int realmsTouched = 0;
        int totalPurged = 0;
        foreach (var realm in realms)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(realm.Slug)) continue;

            try
            {
                var purged = await keyStore.PurgeExpiredRetiredKeysAsync(realm.Slug, ct);
                if (purged > 0)
                {
                    realmsTouched++;
                    totalPurged += purged;
                    logger.LogInformation(
                        "Auth: signing-key janitor purged {Count} expired retired key(s) for realm {Realm}",
                        purged, realm.Slug);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable/broken tenant DB must not abort the whole sweep.
                logger.LogWarning(ex,
                    "Signing-key janitor failed for realm {Realm} — skipping", realm.Slug);
            }
        }

        context.Result = totalPurged == 0
            ? $"No expired signing keys ({realms.Count} realm(s) checked)"
            : $"Purged {totalPurged} expired signing key(s) across {realmsTouched} realm(s)";
    }
}
