using Cocoar.Auth.Authentication.Domain;

namespace Cocoar.Auth.Tests.Unit.Authentication.Domain;

/// <summary>
/// Pins the small computed-property surface on <see cref="EmailOtpChallenge"/>.
/// The constants are part of the public contract — leaking them into the wrong
/// timezone or relaxing the attempt cap is the kind of thing we want loud.
/// </summary>
public class EmailOtpChallengeTests
{
    public class IsExpired
    {
        [Fact]
        public void Future_expiry_is_not_expired()
        {
            var c = new EmailOtpChallenge { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
            Assert.False(c.IsExpired);
        }

        [Fact]
        public void Past_expiry_is_expired()
        {
            var c = new EmailOtpChallenge { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
            Assert.True(c.IsExpired);
        }

        [Fact]
        public void Expiry_exactly_now_is_expired()
        {
            // Comparison is `>=`, so an ExpiresAt strictly in the past is expired.
            // Using a small offset so the relation is deterministic.
            var c = new EmailOtpChallenge { ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(-1) };
            Assert.True(c.IsExpired);
        }

        [Fact]
        public void Default_expiry_is_expired()
        {
            // Default(DateTimeOffset) = year 0001 — far in the past, must read as expired
            // so a freshly default-constructed challenge cannot be used.
            var c = new EmailOtpChallenge();
            Assert.True(c.IsExpired);
        }
    }

    public class HasExceededAttempts
    {
        [Theory]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(3, true)]
        [InlineData(4, true)]
        [InlineData(99, true)]
        public void Returns_true_when_attempts_at_or_above_max(int attempts, bool expected)
        {
            var c = new EmailOtpChallenge { Attempts = attempts };
            Assert.Equal(expected, c.HasExceededAttempts);
        }
    }

    public class Constants
    {
        [Fact]
        public void Max_attempts_is_three()
        {
            // Pinning the policy — relaxing this expands the brute-force surface.
            Assert.Equal(3, EmailOtpChallenge.MaxAttempts);
        }

        [Fact]
        public void Expiration_minutes_is_ten()
        {
            Assert.Equal(10, EmailOtpChallenge.ExpirationMinutes);
        }

        [Fact]
        public void Rate_limit_minutes_is_two()
        {
            Assert.Equal(2, EmailOtpChallenge.RateLimitMinutes);
        }
    }
}
