namespace Modgud.Authorization.Apps;

/// <summary>
/// Well-known <see cref="App.Slug"/> values that the IAM relies on. The
/// <see cref="Modgud"/> slug names the system app — the IAM dogfooding
/// itself — and must always exist in every realm.
/// </summary>
public static class AppSlugs
{
    /// <summary>
    /// The system app: Modgud itself. Hosts the realm-internal
    /// resources (user, oauth-client, session, …) and is seeded automatically
    /// per realm. Cannot be deleted (<see cref="App.IsSystem"/> = true).
    /// </summary>
    public const string Modgud = "modgud";

    /// <summary>
    /// Control-Plane app — owns the cross-realm administration surface (realm
    /// management, future cross-tenant operations). Resources under this slug
    /// are ONLY mounted on requests reaching the configured Control-Plane
    /// hostname; on any other realm's host the endpoints return 404 (see
    /// <c>ControlPlaneGateMiddleware</c> in <c>Modgud.Api</c>).
    ///
    /// <para>The Control-Plane app is seeded into the same tenant DB as
    /// <see cref="Modgud"/> for the realm flagged
    /// <c>Realm.IsControlPlane=true</c>; tenant realms still get the
    /// <see cref="Modgud"/> app but NOT this one — they have no
    /// permissions in the <c>control-plane:</c> namespace, so even the
    /// permission gate would refuse them, even before the routing gate
    /// short-circuits with 404.</para>
    /// </summary>
    public const string ControlPlane = "control-plane";
}
