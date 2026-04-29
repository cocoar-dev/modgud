namespace Cocoar.Auth.Authorization.Access;

/// <summary>
/// An access-policy predicate attached to a group for a specific resource type.
/// Authored in TypeScript, transpiled to JavaScript at save time, translated to
/// a LINQ <c>Expression{Func{TView,bool}}</c> tree at query time via Cocoar.JsEval.Linq.
/// <para>
/// Empty / null script ⇒ unrestricted row-level access for this resource (the
/// group's role grant still gates the action, but no row filter applies).
/// </para>
/// </summary>
public record ResourceAccessScript
{
    public string ResourceType { get; init; } = "";

    /// <summary>User-authored TypeScript source. Shown in admin UI.</summary>
    public string? Script { get; init; }

    /// <summary>JavaScript output of the TypeScript transpiler. What the engine evaluates.</summary>
    public string? CompiledScript { get; init; }
}
