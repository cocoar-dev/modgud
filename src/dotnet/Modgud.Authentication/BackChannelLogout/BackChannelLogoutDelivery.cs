using Modgud.Authentication.Events;

namespace Modgud.Authentication.BackChannelLogout;

/// <summary>
/// ADR 0009 — one pending logout notification to one relying party. Written by the
/// fan-out in the same realm database as the session it belongs to, attempted at once
/// by the in-process dispatcher and, when that fails, again by the per-realm retry
/// job on the schedule in <see cref="BackChannelLogoutConstants.RetrySchedule"/>.
/// Deleted on delivery or after the last attempt; while it exists it is the durable
/// record that a relying party still has to be told. Optimistic concurrency makes the
/// "claim" of an attempt exclusive between the dispatcher and the job.
/// </summary>
public sealed class BackChannelLogoutDelivery
{
    public Guid Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public Guid? SessionId { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public AccessEndScope Scope { get; set; }

    /// <summary>Attempts made so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When the next attempt is due; the retry job picks up everything due.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? LastOutcome { get; set; }
}
