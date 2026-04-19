namespace Cocoar.Auth.Domain.Authorization;

/// <summary>
/// An access policy script for a specific resource type, stored on an AuthorizationGroup.
/// The script is written in TypeScript, transpiled to JavaScript at save time,
/// and evaluated via Cocoar.JsEval at request time.
/// </summary>
public record ResourceAccessScript
{
    public string ResourceType { get; init; } = string.Empty;
    public string? Script { get; init; }
    public string? CompiledScript { get; init; }
}
