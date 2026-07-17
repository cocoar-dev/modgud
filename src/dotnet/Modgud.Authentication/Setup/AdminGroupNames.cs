namespace Modgud.Authentication.Setup;

/// <summary>
/// Well-known name of the realm-admin group seeded by
/// <see cref="RealmAdminBootstrapper"/>.
///
/// <para>English-naming pass (2026-07): the group used to be seeded as
/// <see cref="Legacy"/> ("Administratoren", German). Realms provisioned
/// before this change already contain a group with the legacy name —
/// <see cref="RealmAdminBootstrapper"/> joins it instead of creating a
/// second group, and <c>LegacyAdminGroupRenameBootstrap</c> renames it to
/// <see cref="Current"/> once, idempotently, at boot.</para>
/// </summary>
public static class AdminGroupNames
{
    /// <summary>Name newly-seeded realm-admin groups get from now on.</summary>
    public const string Current = "Administrators";

    /// <summary>
    /// Pre-rename name. Only referenced for backward-compatible lookups and by
    /// the one-time rename migration — never used to seed a new group.
    /// </summary>
    public const string Legacy = "Administratoren";
}
