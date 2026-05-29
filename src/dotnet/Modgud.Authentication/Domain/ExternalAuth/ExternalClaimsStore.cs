namespace Modgud.Authentication.Domain.ExternalAuth;

/// <summary>
/// Federation v1 — per-user snapshot of the claims seen at login, tagged by
/// source. A plain Marten document (NOT event-sourced) keyed on the user id
/// (<see cref="Id"/> == userId), mirroring <c>UserDeletionState</c> so it can be
/// <c>Load</c>ed / <c>Delete</c>d directly.
/// <para>
/// On every successful external login the current provider's entries
/// (<c>source=provider:&lt;slug&gt;</c>) are delete+rewrite refreshed (SET/FORCE
/// reconcile); local + other-provider entries are left untouched. This is the
/// backing data for the in-memory, session-scoped membership derivation — never
/// the source of durable <c>Group.MemberIds</c>.
/// </para>
/// <para>
/// <b>Refreshable snapshot, not an audit trail.</b> It is scrubbed wholesale by a
/// plain <c>Delete</c> on user delete / GDPR erase — there is no event stream to
/// mask (masking rules apply to events only).
/// </para>
/// </summary>
public class ExternalClaimsStore
{
    /// <summary>The Modgud user this snapshot belongs to (equals the user id).</summary>
    public Guid Id { get; set; }

    public List<ClaimEntry> Claims { get; set; } = [];
}

/// <summary>
/// One captured claim value, tagged with its source.
/// <para>
/// <see cref="Source"/> is <c>"local"</c> or <c>"provider:&lt;slug&gt;"</c> (the
/// immutable <c>LoginProvider.Slug</c>). <see cref="CapturedAt"/> is the login
/// timestamp — stored for what-if age and the v2 lease, but <b>not</b> enforced
/// as a drop-timer in v1 (the session is the lease).
/// </para>
/// </summary>
public record ClaimEntry(string Source, string Type, string Value, DateTimeOffset CapturedAt);
