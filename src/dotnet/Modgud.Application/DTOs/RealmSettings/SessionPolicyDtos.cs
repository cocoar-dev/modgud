namespace Modgud.Application.DTOs.RealmSettings;

public record BrowserSessionPolicyDto
{
    public int IdleLifetimeMinutes { get; init; }
    public int AbsoluteLifetimeMinutes { get; init; }
    public bool AllowRememberMe { get; init; }
}

public record UpdateBrowserSessionPolicyDto
{
    public int? IdleLifetimeMinutes { get; init; }
    public int? AbsoluteLifetimeMinutes { get; init; }
    public bool? AllowRememberMe { get; init; }
}

public record ClientSessionPolicyDto
{
    public int IdleLifetimeDays { get; init; }
    public int AbsoluteLifetimeDays { get; init; }
}

public record UpdateClientSessionPolicyDto
{
    public int? IdleLifetimeDays { get; init; }
    public int? AbsoluteLifetimeDays { get; init; }
}
