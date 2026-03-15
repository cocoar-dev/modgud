namespace Cocoar.Auth.Domain.Common;

/// <summary>
/// Constants for custom scope property keys stored in the OpenIddict Properties dictionary.
/// These represent Identity Resource properties from xaidentity mapped onto scopes.
/// </summary>
public static class ScopePropertyKeys
{
	public const string Enabled = "cocoar:enabled";
	public const string Required = "cocoar:required";
	public const string Emphasize = "cocoar:emphasize";
	public const string ShowInDiscoveryDocument = "cocoar:show_in_discovery_document";
	public const string UserClaims = "cocoar:user_claims";
}
