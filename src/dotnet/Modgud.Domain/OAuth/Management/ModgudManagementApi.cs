namespace Modgud.Domain.OAuth.Management;

/// <summary>
/// Stable OAuth contract for Modgud's own management API. The audience selects
/// Modgud as the resource server; the scope only allows a client to target that
/// resource. Fine-grained authorization remains in the Modgud App permission
/// catalog and is evaluated live for the calling Person or ServiceAccount.
/// </summary>
public static class ModgudManagementApi
{
    public const string Audience = "urn:modgud:management-api";
    public const string Scope = "modgud.management";
}
