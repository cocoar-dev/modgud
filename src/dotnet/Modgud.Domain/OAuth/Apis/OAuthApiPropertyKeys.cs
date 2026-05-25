namespace Modgud.Domain.OAuth.Apis;

/// <summary>Custom property keys stored on
/// <see cref="OAuthApiState.Properties"/>. Same JSON-element-value
/// pattern that <c>OAuthApplicationPropertyKeys</c> and
/// <c>ScopePropertyKeys</c> use.</summary>
public static class OAuthApiPropertyKeys
{
    /// <summary>Boolean — when <c>true</c>, this resource server is a
    /// valid <c>resource=</c> target for clients minted via Dynamic
    /// Client Registration. Off by default. One half of the triple
    /// opt-in (realm master + per-Api flag + per-Scope flag). Read by
    /// the resource-indicator handler at token-issue time to enforce
    /// DCR audience containment.</summary>
    public const string AllowDynamicRegistration = "cocoar:allow_dynamic_registration";
}
