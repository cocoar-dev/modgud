namespace Modgud.Authentication.Identity;

/// <summary>
/// B8-P1 — a credential id is stored at most once per realm database. The FIDO2
/// uniqueness callback reads the existing credentials before the insert, so two
/// enrollments of the same authenticator in separate ceremonies (two tabs) could both
/// see none; the unique index decides instead, and the loser of the insert is told
/// the passkey is already registered.
/// </summary>
public static class PasskeyCredentialUniqueness
{
    /// <summary>Name of the unique index on <c>StoredPasskeyCredential.CredentialId</c>.</summary>
    public const string IndexName = "mt_doc_storedpasskeycredential_uidx_credential_id";

    /// <summary>True when <paramref name="ex"/> is a unique violation (23505) of exactly
    /// this index. Marten wraps the Postgres error differently per code path, so the
    /// whole inner-exception chain is walked; any other violation is not swallowed.</summary>
    public static bool IsDuplicateCredentialId(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } pg
                && string.Equals(pg.ConstraintName, IndexName, StringComparison.Ordinal))
                return true;
        return false;
    }
}
