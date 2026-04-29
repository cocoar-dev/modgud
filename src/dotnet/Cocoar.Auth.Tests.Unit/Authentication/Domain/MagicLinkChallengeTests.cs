using Cocoar.Auth.Authentication.Domain;

namespace Cocoar.Auth.Tests.Unit.Authentication.Domain;

/// <summary>
/// Pins the public surface on <see cref="MagicLinkChallenge"/>. Like
/// <c>EmailOtpChallenge</c> the constants are policy and worth nailing down.
/// </summary>
public class MagicLinkChallengeTests
{
    public class IsExpired
    {
        [Fact]
        public void Future_expiry_is_not_expired()
        {
            var c = new MagicLinkChallenge { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
            Assert.False(c.IsExpired);
        }

        [Fact]
        public void Past_expiry_is_expired()
        {
            var c = new MagicLinkChallenge { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
            Assert.True(c.IsExpired);
        }

        [Fact]
        public void Default_expiry_is_expired()
        {
            var c = new MagicLinkChallenge();
            Assert.True(c.IsExpired);
        }
    }

    public class Constants
    {
        [Fact]
        public void Expiration_minutes_is_fifteen()
        {
            Assert.Equal(15, MagicLinkChallenge.ExpirationMinutes);
        }

        [Fact]
        public void Rate_limit_minutes_is_two()
        {
            Assert.Equal(2, MagicLinkChallenge.RateLimitMinutes);
        }
    }
}
