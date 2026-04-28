namespace TimeToDo.Authentication;

public interface IMagicLinkConfiguration
{
    bool Enabled { get; }
    int ExpirationMinutes { get; }
    int RateLimitMinutes { get; }
}
