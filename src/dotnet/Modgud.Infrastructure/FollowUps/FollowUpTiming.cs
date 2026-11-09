using System.Globalization;
using Wolverine;

namespace Modgud.Infrastructure.FollowUps;

/// <summary>
/// The "not before" of a safety net (<see cref="IFollowUpOutbox.PublishSafetyNetAsync{T}"/>). The
/// message is enqueued right after the commit and its handler waits here until it is due. Wolverine's
/// own scheduling is not used: scheduled local messages are picked up by a database poll, which
/// delivered them half a minute late. The envelope stays in the durable inbox meanwhile, so a
/// restart resumes the wait instead of losing the message; a retry does not wait again once due.
/// </summary>
public static class FollowUpTiming
{
    public const string NotBeforeHeader = "modgud-follow-up-not-before";

    public static string NotBefore(TimeSpan delay) =>
        DateTimeOffset.UtcNow.Add(delay).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Waits until the envelope's <see cref="NotBeforeHeader"/>; returns at once without it.</summary>
    public static Task WaitUntilDueAsync(Envelope envelope, CancellationToken ct)
    {
        if (!envelope.TryGetHeader(NotBeforeHeader, out var raw)
            || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var notBefore))
            return Task.CompletedTask;
        var wait = notBefore - DateTimeOffset.UtcNow;
        return wait > TimeSpan.Zero ? Task.Delay(wait, ct) : Task.CompletedTask;
    }
}
