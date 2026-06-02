using Microsoft.Extensions.Logging;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Npgsql;

namespace Modgud.Infrastructure.Migrations;

/// <summary>
/// TEMPORARY one-time, self-removing per-realm migration that establishes the
/// email-uniqueness invariant (Account-Lifecycle plan, WS2). For each realm DB it:
/// <list type="number">
///   <item>nulls the cleartext <c>Email</c>/<c>NormalizedEmail</c> left on
///   already-deleted users by the legacy admin-delete path (PII cleanup);</item>
///   <item>scans for <em>active</em> email duplicates and — if any exist — logs a
///   loud WARNING and <b>refuses</b> to build the unique index (duplicates need
///   human resolution, and a blind <c>CREATE UNIQUE INDEX</c> would crash boot);</item>
///   <item>otherwise builds the partial unique index
///   <c>WHERE NOT is_deleted</c> on <c>NormalizedEmail</c>, which reserves the
///   email across the active + both pending states and releases it only at the
///   permanent erase (where the email is nulled and <c>IsDeleted</c> flips true).</item>
/// </list>
///
/// <para>It runs against raw Npgsql connections (one per realm DB), bypassing
/// Marten tenancy — so the index is built out-of-band and CAN be conditionally
/// skipped, which a declarative Marten index could not. It emits a nag WARNING on
/// every boot so it gets removed once every realm reports the index present; at
/// that point the index moves to the declarative Marten config and this class
/// (plus its bootstrap call) is deleted. Tracked as a removal TODO in the plan.</para>
/// </summary>
public sealed class EmailUniquenessMigration(
    IRealmProvisioningService realms,
    IMasterConnectionString masterCs,
    ILogger<EmailUniquenessMigration> logger)
{
    /// <summary>Name of the partial unique index this migration owns until removal.</summary>
    public const string IndexName = "mt_unique_idx_applicationuser_email_active";

    private const string ScrubDeletedEmailsSql = """
        UPDATE public.mt_doc_applicationuser
        SET data = jsonb_set(jsonb_set(data, '{Email}', 'null'::jsonb, true),
                             '{NormalizedEmail}', 'null'::jsonb, true),
            mt_last_modified = transaction_timestamp()
        WHERE COALESCE((data ->> 'IsDeleted')::boolean, false) = true
          AND (data ->> 'NormalizedEmail' IS NOT NULL OR data ->> 'Email' IS NOT NULL);
        """;

    private const string ActiveDuplicatesSql = """
        SELECT data ->> 'NormalizedEmail' AS email, count(*) AS cnt
        FROM public.mt_doc_applicationuser
        WHERE COALESCE((data ->> 'IsDeleted')::boolean, false) = false
          AND data ->> 'NormalizedEmail' IS NOT NULL
        GROUP BY data ->> 'NormalizedEmail'
        HAVING count(*) > 1;
        """;

    private static readonly string CreateIndexSql = $"""
        CREATE UNIQUE INDEX IF NOT EXISTS {IndexName}
        ON public.mt_doc_applicationuser ((data ->> 'NormalizedEmail'))
        WHERE COALESCE((data ->> 'IsDeleted')::boolean, false) = false
          AND data ->> 'NormalizedEmail' IS NOT NULL;
        """;

    public async Task RunAsync(CancellationToken ct = default)
    {
        var allRealms = await realms.GetAllRealmsAsync(ct);
        var slugs = allRealms.Select(r => r.Slug).ToList();
        // Defensive: the system realm is always present, but make sure its DB is
        // covered even if the registry read came back empty for any reason.
        if (!slugs.Contains(TenantConstants.SystemTenantId))
            slugs.Insert(0, TenantConstants.SystemTenantId);

        var migrated = 0;
        var refused = 0;
        foreach (var slug in slugs)
        {
            try
            {
                if (await MigrateRealmAsync(slug, ct)) migrated++;
                else refused++;
            }
            catch (Exception ex)
            {
                refused++;
                logger.LogError(ex,
                    "[EmailUniquenessMigration] Failed for realm {Slug} — its email-uniqueness index was not established.",
                    slug);
            }
        }

        // Nag on every boot: this temporary migration is still wired in. Once
        // every realm reports the index present (refused == 0 across a clean
        // run), remove EmailUniquenessMigration + its bootstrap call and move
        // the index to the declarative Marten config. See plan WS2.
        logger.LogWarning(
            "[EmailUniquenessMigration] TEMPORARY one-time migration ran across {Total} realm(s): " +
            "{Migrated} with the unique email index in place, {Refused} unresolved. " +
            "Remove this migration once all realms report the index present.",
            slugs.Count, migrated, refused);
    }

    /// <summary>Returns <c>true</c> when the realm ends with the unique index in
    /// place (built now or already present), <c>false</c> when it was skipped
    /// (table absent or active duplicates need resolution).</summary>
    private async Task<bool> MigrateRealmAsync(string slug, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionStringFor(slug));
        await conn.OpenAsync(ct);

        // The ApplicationUser table may not exist on a brand-new DB whose schema
        // hasn't been applied yet — skip; the next boot re-runs after schema apply.
        if (await ScalarIsNull(conn, "SELECT to_regclass('public.mt_doc_applicationuser')::text", ct))
        {
            logger.LogInformation(
                "[EmailUniquenessMigration] Realm {Slug}: ApplicationUser table not present yet — skipping.", slug);
            return false;
        }

        // 1) Clear legacy cleartext PII on already-deleted users.
        await using (var scrub = new NpgsqlCommand(ScrubDeletedEmailsSql, conn))
        {
            var affected = await scrub.ExecuteNonQueryAsync(ct);
            if (affected > 0)
                logger.LogWarning(
                    "[EmailUniquenessMigration] Realm {Slug}: nulled leftover email on {Count} already-deleted user(s).",
                    slug, affected);
        }

        // 2) Already built on a previous boot? Nothing more to do.
        if (!await ScalarIsNull(conn, $"SELECT to_regclass('public.{IndexName}')::text", ct))
            return true;

        // 3) Refuse to build the index while active duplicates exist.
        var dups = new List<string>();
        await using (var dupCmd = new NpgsqlCommand(ActiveDuplicatesSql, conn))
        await using (var reader = await dupCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                dups.Add($"{reader.GetString(0)} (x{reader.GetInt64(1)})");
        }

        if (dups.Count > 0)
        {
            logger.LogWarning(
                "[EmailUniquenessMigration] Realm {Slug}: REFUSING to build the email-uniqueness index — " +
                "{Count} active email duplicate(s) need manual resolution (rename/merge/delete, then reboot): {Dups}",
                slug, dups.Count, string.Join("; ", dups));
            return false;
        }

        // 4) Build the partial unique index.
        // CA2100: CreateIndexSql is a compile-time constant interpolating only
        // the const IndexName — no runtime/user input reaches the SQL text.
#pragma warning disable CA2100
        await using (var create = new NpgsqlCommand(CreateIndexSql, conn))
#pragma warning restore CA2100
            await create.ExecuteNonQueryAsync(ct);
        logger.LogInformation(
            "[EmailUniquenessMigration] Realm {Slug}: built unique email index '{Index}'.", slug, IndexName);
        return true;
    }

    private static async Task<bool> ScalarIsNull(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        // CA2100: callers pass only compile-time-constant SQL (to_regclass
        // probes interpolating the const IndexName) — no user input.
#pragma warning disable CA2100
        await using var cmd = new NpgsqlCommand(sql, conn);
#pragma warning restore CA2100
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull;
    }

    private string ConnectionStringFor(string slug)
    {
        var builder = new NpgsqlConnectionStringBuilder(masterCs.Value);
        // Every realm — including the system realm — lives in its own
        // {master}_{slug} database (see RealmProvisioningService + the boot
        // block's system-tenant registration). The master DB itself is pure
        // control-plane infrastructure and holds no ApplicationUser table.
        builder.Database = $"{builder.Database}_{slug}";
        return builder.ConnectionString;
    }
}
