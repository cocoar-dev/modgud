namespace Modgud.Authentication.Domain;

/// <summary>
/// Authoritative server-side continuation state for one native OAuth
/// client/device. The associated refresh-token family is rooted in a unique
/// OpenIddict authorization so this row can be revoked independently.
/// </summary>
public class ClientSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string OAuthApplicationId { get; set; } = string.Empty;
    public string AuthorizationId { get; set; } = string.Empty;
    public string? ClientDisplayName { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Browser { get; set; }
    public string? BrowserVersion { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? DeviceType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsActive(DateTimeOffset now) => ExpiresAt > now && AbsoluteExpiresAt > now;

    public void Touch(DateTimeOffset now, TimeSpan idleLifetime)
    {
        LastActiveAt = now;
        var idleExpiry = now.Add(idleLifetime);
        ExpiresAt = idleExpiry <= AbsoluteExpiresAt ? idleExpiry : AbsoluteExpiresAt;
    }
}
