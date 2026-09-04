namespace Modgud.Domain.OAuth.Applications;

/// <summary>
/// ADR 0009 — outcome of the last logout-token POST to a client's back-channel logout
/// URI. A separate document keyed by the client's id, written by the delivery worker:
/// the client document itself is never rewritten from a background job (a whole-document
/// store would silently clobber a concurrent admin edit).
/// </summary>
public sealed class BackChannelLogoutDeliveryStatus
{
    public const string Delivered = "delivered";

    /// <summary>= <c>OAuthApplicationState.Id</c>.</summary>
    public Guid Id { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public DateTimeOffset LastAttemptAt { get; set; }

    /// <summary><c>delivered</c>, or <c>failed:&lt;reason&gt;</c> (<c>http-503</c>,
    /// <c>timeout</c>, <c>connect</c>, <c>ssrf</c>).</summary>
    public string LastOutcome { get; set; } = string.Empty;

    /// <summary>1-based attempt number of the last try (retries count up).</summary>
    public int Attempt { get; set; }

    public string? TargetUri { get; set; }
}
