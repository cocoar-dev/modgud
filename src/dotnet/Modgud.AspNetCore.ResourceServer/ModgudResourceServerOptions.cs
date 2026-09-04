using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Modgud.AspNetCore.ResourceServer;

/// <summary>The access-token formats accepted by a Modgud resource server.</summary>
public enum ModgudTokenMode
{
    /// <summary>Accept only self-contained JWT access tokens.</summary>
    OnlyJwt,

    /// <summary>Accept only opaque reference tokens through introspection.</summary>
    OnlyReferenceToken,

    /// <summary>Accept both formats and route each token to the matching validator.</summary>
    Both,
}

/// <summary>Defaults for the single Modgud resource-server authentication scheme.</summary>
public static class ModgudResourceServerDefaults
{
    /// <summary>The public scheme registered by <c>AddModgudResourceServer</c>.</summary>
    public const string AuthenticationScheme = "Modgud";
}

/// <summary>Configuration for a Modgud-protected ASP.NET Core resource server.</summary>
public sealed class ModgudResourceServerOptions
{
    /// <summary>The realm host root, for example <c>https://id.example.com</c>.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>The resource-server audience expected in every accepted token.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>The accepted token format. Defaults to local JWT validation.</summary>
    public ModgudTokenMode TokenMode { get; set; } = ModgudTokenMode.OnlyJwt;

    /// <summary>
    /// Confidential introspection client ID. Defaults to <see cref="Audience"/>.
    /// Used only by <see cref="ModgudTokenMode.OnlyReferenceToken"/> and
    /// <see cref="ModgudTokenMode.Both"/>.
    /// </summary>
    public string? IntrospectionClientId { get; set; }

    /// <summary>
    /// Confidential introspection client secret. Required by
    /// <see cref="ModgudTokenMode.OnlyReferenceToken"/> and
    /// <see cref="ModgudTokenMode.Both"/>.
    /// </summary>
    public string? IntrospectionClientSecret { get; set; }

    /// <summary>
    /// Requires an HTTPS authority. Disable only for local development.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Rejects JWT access tokens of sessions that ended before the token expired,
    /// fed by the Modgud Application change feed. Off by default; see
    /// <see cref="ModgudSessionRevocationOptions"/>. Used only by modes that accept JWTs
    /// (reference tokens are revoked through introspection already).
    /// </summary>
    public ModgudSessionRevocationOptions SessionRevocation { get; set; } = new();

    /// <summary>
    /// Optional advanced configuration applied to the internal JWT bearer
    /// handler before Modgud wires DPoP and audience-local claims projection.
    /// Used only by modes that accept JWTs.
    /// </summary>
    public Action<JwtBearerOptions>? ConfigureJwtBearer { get; set; }
}

internal sealed class ModgudIntrospectionOptions : AuthenticationSchemeOptions
{
    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}

internal static class ModgudSchemeNames
{
    public const string Jwt = "Modgud.Internal.Jwt";
    public const string Introspection = "Modgud.Internal.Introspection";
}
