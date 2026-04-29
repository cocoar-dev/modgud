namespace Cocoar.Auth.Authentication.Sessions;

public record SessionDto
{
    public required string Id { get; init; }
    public string? IpAddress { get; init; }
    public string? Browser { get; init; }
    public string? BrowserVersion { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OsVersion { get; init; }
    public string? DeviceType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastActiveAt { get; init; }
    public bool IsCurrent { get; init; }
}

public record SessionListDto
{
    public required List<SessionDto> Sessions { get; init; }
}
