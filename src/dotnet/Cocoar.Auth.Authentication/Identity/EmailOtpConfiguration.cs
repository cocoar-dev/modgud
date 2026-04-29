namespace Cocoar.Auth.Authentication.Identity;

public class EmailOtpConfiguration
{
    public int ExpirationMinutes { get; set; } = 10;
    public int RateLimitMinutes { get; set; } = 2;
    public int MaxAttempts { get; set; } = 3;
}
