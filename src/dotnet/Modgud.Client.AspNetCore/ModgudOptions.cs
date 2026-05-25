namespace Modgud.Client.AspNetCore;

/// <summary>
/// Configuration for the Modgud resource-server integration.
///
/// <para>The lib does two things on top of vanilla
/// <c>AddJwtBearer</c>:</para>
/// <list type="bullet">
///   <item>Wires a <c>JwtBearerEvents.OnTokenValidated</c> handler that
///   fetches <c>{Authority}/connect/userinfo</c> with the user's token
///   and adds the <c>resource_access</c> claim to the principal — pure
///   <c>AddJwtBearer</c> doesn't do this natively (UserInfo-fetching is
///   an <c>AddOpenIdConnect</c> feature).</item>
///   <item>Registers a <c>ClaimsTransformation</c> that reads
///   <c>resource_access[<see cref="Audience"/>]</c> off the principal
///   and projects roles / permissions / groups onto flat
///   <c>ClaimTypes.Role</c> / <c>"permission"</c> / <c>"group"</c> claims
///   so endpoint filters + <c>[Authorize(Roles=...)]</c> work natively.</item>
/// </list>
///
/// <para>UserInfo emits permissions in their bypass-pre-expanded form
/// (the IdP already resolves <c>realm:admin</c> and <c>&lt;r&gt;:admin</c>
/// to concrete catalog strings), so the lib doesn't need to evaluate
/// bypass tiers itself — exact-match is sufficient.</para>
/// </summary>
public sealed class ModgudOptions
{
    /// <summary>
    /// The audience this resource server identifies as — same value the
    /// JWT-bearer middleware compares the token's <c>aud</c> claim against
    /// (<c>options.Audience</c> on <c>AddJwtBearer</c>). Used as the lookup
    /// key into <c>resource_access[…]</c> on the principal's claims.
    ///
    /// <para>Required.</para>
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// IdP base URL used to construct the UserInfo URL
    /// (<c>{Authority}/connect/userinfo</c>). Same value as
    /// <c>JwtBearerOptions.Authority</c>. Trailing slashes are tolerated.
    ///
    /// <para>Required.</para>
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Authentication scheme to attach the UserInfo-fetching handler to.
    /// Defaults to <c>"Bearer"</c>; override if your host uses a custom
    /// scheme name on <c>AddJwtBearer(scheme, …)</c>.
    /// </summary>
    public string JwtBearerScheme { get; set; } = "Bearer";
}
