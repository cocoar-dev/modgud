using Marten;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Setup;

/// <summary>
/// One-time, idempotent boot migration (0.19 → 0.20): the App setting
/// <c>SignIn.PasskeyAndroidOrigins</c> (0.19, Android only) became
/// <c>SignIn.PasskeyAppOrigins</c> (any native app origin). The stored values are already
/// in the new form (<c>android:apk-key-hash:…</c>), so the entries move over unchanged and
/// the old property is removed. <see cref="Modgud.Domain.Applications.ApplicationSettings"/>
/// is a plain document, so one JSON update per realm does it; a repeat boot finds no
/// document with the old property and changes nothing.
///
/// <para>Same cold-start-walks-every-realm shape as <see cref="PushedAuthorizationPermissionBackfill"/>:
/// re-enter <see cref="TenantContext"/> per realm so the tenant-scoped session writes the
/// correct database. Hosted services start before the server accepts requests, so no
/// ceremony reads a settings document in between.</para>
/// </summary>
public class PasskeyAppOriginsRename(
    IServiceScopeFactory scopeFactory,
    IRealmCache realmCache,
    ILogger<PasskeyAppOriginsRename> logger) : IHostedService
{
    private const string MoveSql =
        """
        update mt_doc_applicationsettings
        set data = jsonb_set(
            data #- '{SignIn,PasskeyAndroidOrigins}',
            '{SignIn,PasskeyAppOrigins}',
            coalesce(data->'SignIn'->'PasskeyAppOrigins', data->'SignIn'->'PasskeyAndroidOrigins'))
        where jsonb_typeof(data->'SignIn') = 'object'
          and jsonb_exists(data->'SignIn', 'PasskeyAndroidOrigins')
        """;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var realm in await realmCache.GetAllActiveAsync())
        {
            try
            {
                using var _ = TenantContext.Enter(realm.Slug);
                using var scope = scopeFactory.CreateScope();
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

                // A realm that never stored App settings has no table yet.
                var hasTable = await session.QueryAsync<bool>(
                    "select to_regclass('mt_doc_applicationsettings') is not null", cancellationToken);
                if (!hasTable.FirstOrDefault()) continue;

                var pending = await session.QueryAsync<int>(
                    "select count(*)::int from mt_doc_applicationsettings where jsonb_typeof(data->'SignIn') = 'object' and jsonb_exists(data->'SignIn', 'PasskeyAndroidOrigins')",
                    cancellationToken);
                if (pending.FirstOrDefault() == 0) continue;

                session.QueueSqlCommand(MoveSql);
                await session.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Moved SignIn.PasskeyAndroidOrigins to SignIn.PasskeyAppOrigins on {Count} app(s) in realm {Realm}",
                    pending.First(), realm.Slug);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Passkey app origins rename failed for realm {Realm} — it retries on the next boot",
                    realm.Slug);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
