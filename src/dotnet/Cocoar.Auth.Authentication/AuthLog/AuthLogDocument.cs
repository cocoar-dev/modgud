namespace Cocoar.Auth.Authentication.AuthLog;

/// <summary>
/// Marten document for persisted auth log entries (7-day retention).
/// </summary>
public class AuthLogDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = "Info";
    public string Message { get; init; } = "";
    public string? UserName { get; init; }
    public string? Ip { get; init; }
}
