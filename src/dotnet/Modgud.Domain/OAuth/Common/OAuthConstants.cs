namespace Modgud.Domain.OAuth.Common;

/// <summary>
/// String constants mirroring OpenIddict's permission prefix scheme. Kept in
/// Domain so the admin slice can build permission lists without taking a
/// dependency on the OpenIddict abstractions package (which only enters in
/// etappe 3b alongside the runtime).
/// </summary>
public static class OAuthPermissions
{
    public static class Prefixes
    {
        public const string Scope = "scp:";
        public const string GrantType = "gt:";
        public const string ResponseType = "rst:";
        public const string Endpoint = "ept:";
    }

    public static class Endpoints
    {
        public const string Authorization = "ept:authorization";
        public const string Token = "ept:token";
        public const string EndSession = "ept:logout";
        public const string Introspection = "ept:introspection";
        public const string Revocation = "ept:revocation";
        public const string DeviceAuthorization = "ept:device_authorization";
    }

    public static class GrantTypes
    {
        public const string AuthorizationCode = "gt:authorization_code";
        public const string ClientCredentials = "gt:client_credentials";
        public const string RefreshToken = "gt:refresh_token";
        public const string Implicit = "gt:implicit";
        public const string Password = "gt:password";
        public const string DeviceCode = "gt:urn:ietf:params:oauth:grant-type:device_code";
    }

    public static class ResponseTypes
    {
        public const string Code = "rst:code";
    }
}

public static class OAuthClientTypes
{
    public const string Public = "public";
    public const string Confidential = "confidential";
}

public static class OAuthConsentTypes
{
    public const string Explicit = "explicit";
    public const string Implicit = "implicit";
    public const string External = "external";
    public const string Systematic = "systematic";
}

/// <summary>
/// Wire-format values for the OIDC <c>application_type</c> client metadata
/// field. Per the OIDC Dynamic Client Registration spec — must be the literal
/// strings, lowercase, exact.
/// </summary>
public static class OAuthApplicationTypes
{
    public const string Web = "web";
    public const string Native = "native";
}
