namespace Cocoar.Auth.Authorization.Apps;

/// <summary>
/// Well-known <see cref="App.Slug"/> values that the IAM relies on. The
/// <see cref="CocoarAuth"/> slug names the system app — the IAM dogfooding
/// itself — and must always exist in every realm.
/// </summary>
public static class AppSlugs
{
    /// <summary>
    /// The system app: Cocoar.Auth itself. Hosts the realm-internal
    /// resources (user, oauth-client, session, …) and is seeded automatically
    /// per realm. Cannot be deleted (<see cref="App.IsSystem"/> = true).
    /// </summary>
    public const string CocoarAuth = "cocoar-auth";
}
