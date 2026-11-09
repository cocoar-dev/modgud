namespace Modgud.Infrastructure.FollowUps;

/// <summary>
/// Thrown by a follow-up handler when retrying cannot help (the data the effect needs is gone or
/// invalid). The message is moved to the dead letters at once instead of going through the
/// retry schedule, where the dead-letter health check reports it.
/// </summary>
public sealed class FollowUpPermanentException(string message, Exception? inner = null) : Exception(message, inner);
