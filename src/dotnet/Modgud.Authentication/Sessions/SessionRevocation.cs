namespace Modgud.Authentication.Sessions;

/// <summary>
/// Bounded recovery history for SDK denylist initialization. Written atomically
/// with grant removal, independently of change-feed generation/net projections.
/// Only the App and grant session identifier are retained, not the user or token
/// payload. This does not repair native/public-sid correlation (CS02).
/// </summary>
public sealed class SessionRevocation
{
    public const int RetentionSeconds = 7 * 24 * 60 * 60;

    public Guid Id { get; set; }
    public Guid AppId { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset EndedAt { get; set; }
}
