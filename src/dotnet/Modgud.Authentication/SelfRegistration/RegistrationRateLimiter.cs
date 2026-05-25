using System.Collections.Concurrent;

namespace Modgud.Authentication.SelfRegistration;

/// <summary>
/// In-memory per-email registration rate-limiter. Sliding window
/// (default 1 attempt per email per minute, 3 per hour). Resets on app
/// restart — good enough for MVP; if multi-instance deployments need
/// real persistence we can swap a Redis/Marten-backed implementation
/// behind the same surface.
///
/// <para>Rate-limit per email, not per IP, to avoid NAT-lockout for
/// legitimate users sharing an egress IP (corp, university, mobile
/// carrier). Email-based is also harder to abuse — an attacker would
/// need a fresh email per attempt, which has its own cost.</para>
/// </summary>
public sealed class RegistrationRateLimiter
{
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ShortWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LongWindow = TimeSpan.FromHours(1);
    private const int ShortLimit = 1;
    private const int LongLimit = 3;

    public bool TryConsume(string email)
    {
        var now = DateTimeOffset.UtcNow;
        var list = _attempts.GetOrAdd(email, _ => new List<DateTimeOffset>());
        lock (list)
        {
            list.RemoveAll(t => now - t > LongWindow);

            var inShort = list.Count(t => now - t <= ShortWindow);
            var inLong = list.Count;
            if (inShort >= ShortLimit || inLong >= LongLimit) return false;

            list.Add(now);
            return true;
        }
    }
}
