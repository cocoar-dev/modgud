namespace Modgud.Authentication;

public interface IServerConfiguration
{
    /// <summary>
    /// The URL Kestrel binds to (<c>app.Run(AppUrl)</c>). This is the bind
    /// address only — it is NOT the public-facing origin. All outbound
    /// user-facing links and the WebAuthn relying party are keyed off the
    /// per-realm <c>Realm.PrimaryDomain</c> (see <c>RealmPublicUrl</c>), not
    /// off this value.
    /// </summary>
    string AppUrl { get; }
}
