using ErrorOr;
using Marten;
using Modgud.Authentication.Domain.LoginProviders;

namespace Modgud.Authentication.Api.Admin.LoginProviders.Commands;

/// <summary>
/// EP-04 — the database decides what a check-then-write cannot: the unique indexes on the
/// active slug and display name (see <c>MartenStoreOptionsExtensions</c>) and the
/// expected stream version of an update. A save that loses such a race comes back as the
/// same conflict the up-front checks report, never as an unhandled server error.
/// </summary>
public static class LoginProviderWriteConflicts
{
    public const string ActiveSlugIndex = "mt_doc_loginprovider_uidx_active_slug";
    public const string ActiveDisplayNameIndex = "mt_doc_loginprovider_uidx_active_display_name";

    /// <summary>Predicate of both indexes: only providers that are not deleted.</summary>
    public const string NotDeletedPredicate = "coalesce(data ->> 'IsDeleted', 'false') = 'false'";

    public static async Task<Error?> SaveAsync(IDocumentSession session, string slug, string displayName, CancellationToken ct)
    {
        try
        {
            await session.SaveChangesAsync(ct);
            return null;
        }
        catch (Exception ex) when (Classify(ex) is { } error)
        {
            return error switch
            {
                Conflict.Slug => Error.Conflict("LoginProvider.SlugTaken",
                    $"A login provider with slug '{slug}' already exists in this realm."),
                Conflict.DisplayName => Error.Conflict("LoginProvider.DisplayNameTaken",
                    $"A login provider named '{displayName}' already exists."),
                _ => Error.Conflict("LoginProvider.ConcurrentChange",
                    "The login provider was changed meanwhile. Reload it and apply your change again."),
            };
        }
    }

    /// <summary>True when <paramref name="ex"/> is a violation of the active-slug index —
    /// the seeder uses it to tell "another instance seeded first" apart.</summary>
    public static bool IsActiveSlugConflict(Exception ex) => Classify(ex) == Conflict.Slug;

    private enum Conflict { Slug, DisplayName, StreamVersion }

    private static Conflict? Classify(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } pg)
            {
                if (pg.ConstraintName == ActiveSlugIndex) return Conflict.Slug;
                if (pg.ConstraintName == ActiveDisplayNameIndex) return Conflict.DisplayName;
            }
            if (ex is JasperFx.ConcurrencyException or JasperFx.Events.EventStreamUnexpectedMaxEventIdException)
                return Conflict.StreamVersion;
        }
        return null;
    }
}
