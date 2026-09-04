namespace Modgud.AspNetCore.ResourceServer;

/// <summary>
/// Rejects JWT access tokens whose session ended before the token expired, using the
/// Modgud Application change feed (<c>session</c> entities) as the source. Without
/// this a self-contained JWT stays valid until <c>exp</c> even after logout, force
/// sign-out, deactivation or deletion; reference tokens are unaffected (introspection
/// already answers "inactive" immediately).
/// </summary>
/// <remarks>
/// The library follows the feed of one Application with a <c>client_credentials</c>
/// token (scope <c>modgud.management</c>, permission <c>app-scope:read</c>, the client
/// assigned to the Application) and keeps an in-memory denylist of ended session ids
/// for the access-token lifetime. Fail-open by design: while the feed is unreachable,
/// tokens are validated as before and the worker keeps retrying; the gap is bounded by
/// the token lifetime, exactly as without the feature.
/// </remarks>
public sealed class ModgudSessionRevocationOptions
{
    /// <summary>Turns the feature on. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>The Modgud Application (ShortGuid id as shown in the admin UI / API)
    /// whose change feed lists the sessions of this resource server's clients.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>Confidential client with the <c>client_credentials</c> grant, the
    /// <c>modgud.management</c> scope and, through its service account, the
    /// <c>app-scope:read</c> permission on <see cref="AppId"/>. Defaults to
    /// <see cref="ModgudResourceServerOptions.IntrospectionClientId"/> when set.</summary>
    public string? ClientId { get; set; }

    /// <summary>Secret of <see cref="ClientId"/>. Defaults to
    /// <see cref="ModgudResourceServerOptions.IntrospectionClientSecret"/> when set.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>How long an ended session id is kept on the denylist — at least the
    /// longest access-token lifetime the realm issues to this audience (default 60 min
    /// plus the clock skew below). An entry can be dropped once every token that
    /// could carry it has expired.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Extra time an entry stays on the denylist beyond the lifetime, covering
    /// clock skew between the IdP and this host. Default 5 min.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay between two polls when the previous batch was not full. Default 5 s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Delay after a failed request (token, snapshot or poll) before retrying.
    /// Default 15 s.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Messages per poll (1–500). Default 200.</summary>
    public int BatchSize { get; set; } = 200;
}
