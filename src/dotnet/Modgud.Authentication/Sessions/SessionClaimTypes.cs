namespace Modgud.Authentication.Sessions;

public static class SessionClaimTypes
{
    public const string BrowserSessionId = "modgud.session_id";
    public const string ClientSessionId = "modgud.client_session_id";

    /// <summary>ADR 0009 — the OpenID Connect session identifier that reaches ID tokens,
    /// access tokens and introspection: <c>UserSession.Id</c> for browser flows,
    /// <c>ClientSession.Id</c> for native grants. The value a logout token repeats.</summary>
    public const string Sid = "sid";
}
