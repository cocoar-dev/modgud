using Cocoar.Auth.Authentication.Identity;

namespace Cocoar.Auth.Tests.Unit.Authentication.Identity;

/// <summary>
/// Pins the policy defaults baked into <see cref="EmailOtpConfiguration"/>.
/// These mirror the constants on <c>EmailOtpChallenge</c>; if they ever drift
/// apart silently a code path uses one and another uses the other, so test
/// both sides.
/// </summary>
public class EmailOtpConfigurationTests
{
    [Fact]
    public void Defaults_match_the_email_otp_challenge_constants()
    {
        var cfg = new EmailOtpConfiguration();

        Assert.Equal(10, cfg.ExpirationMinutes);
        Assert.Equal(2, cfg.RateLimitMinutes);
        Assert.Equal(3, cfg.MaxAttempts);
    }

    [Fact]
    public void Properties_are_settable_for_configuration_binding()
    {
        // The class is bound from settings — make sure the setters are still
        // there (a future "init-only" rewrite would break configuration).
        var cfg = new EmailOtpConfiguration
        {
            ExpirationMinutes = 5,
            RateLimitMinutes = 1,
            MaxAttempts = 7,
        };

        Assert.Equal(5, cfg.ExpirationMinutes);
        Assert.Equal(1, cfg.RateLimitMinutes);
        Assert.Equal(7, cfg.MaxAttempts);
    }
}
