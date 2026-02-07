using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Domain.Aggregates;

/// <summary>
/// Aggregate for OAuth applications (clients).
/// Sensitive data (ClientSecret, JsonWebKeySet) is stored separately in OAuthApplicationSecurityData.
/// </summary>
public class OAuthApplicationAggregate
{
	public Guid Id { get; private set; }
	public string ClientId { get; private set; } = string.Empty;
	public string? DisplayName { get; private set; }
	public string? ClientType { get; private set; }
	public string? ConsentType { get; private set; }
	public string? ApplicationType { get; private set; }
	public List<string> RedirectUris { get; private set; } = new();
	public List<string> PostLogoutRedirectUris { get; private set; } = new();
	public List<string> Permissions { get; private set; } = new();
	public List<string> Requirements { get; private set; } = new();
	public Dictionary<string, string> Settings { get; private set; } = new();
	public Dictionary<string, string> DisplayNames { get; private set; } = new();
	public Dictionary<string, object?> Properties { get; private set; } = new();
	public bool IsDeleted { get; private set; }

	// For Marten event sourcing
	public OAuthApplicationAggregate() { }

	public static (OAuthApplicationAggregate, OAuthApplicationCreated) Create(
		Guid id,
		string clientId,
		string? displayName,
		string? clientType,
		string? consentType,
		string? applicationType,
		IReadOnlyList<string> redirectUris,
		IReadOnlyList<string> postLogoutRedirectUris,
		IReadOnlyList<string> permissions,
		IReadOnlyList<string> requirements)
	{
		var aggregate = new OAuthApplicationAggregate();
		var @event = new OAuthApplicationCreated(
			id,
			clientId,
			displayName,
			clientType,
			consentType,
			applicationType,
			redirectUris,
			postLogoutRedirectUris,
			permissions,
			requirements);

		aggregate.Apply(@event);
		return (aggregate, @event);
	}

	public OAuthApplicationDisplayNameChanged SetDisplayName(string? displayName)
	{
		var @event = new OAuthApplicationDisplayNameChanged(Id, displayName);
		Apply(@event);
		return @event;
	}

	public OAuthApplicationClientTypeChanged SetClientType(string? clientType)
	{
		var @event = new OAuthApplicationClientTypeChanged(Id, clientType);
		Apply(@event);
		return @event;
	}

	public OAuthApplicationConsentTypeChanged SetConsentType(string? consentType)
	{
		var @event = new OAuthApplicationConsentTypeChanged(Id, consentType);
		Apply(@event);
		return @event;
	}

	public OAuthApplicationRedirectUrisChanged SetRedirectUris(IReadOnlyList<string> redirectUris)
	{
		var @event = new OAuthApplicationRedirectUrisChanged(Id, redirectUris);
		Apply(@event);
		return @event;
	}

	public OAuthApplicationPostLogoutRedirectUrisChanged SetPostLogoutRedirectUris(IReadOnlyList<string> uris)
	{
		var @event = new OAuthApplicationPostLogoutRedirectUrisChanged(Id, uris);
		Apply(@event);
		return @event;
	}

	public OAuthApplicationPermissionsChanged SetPermissions(IReadOnlyList<string> permissions)
	{
		var @event = new OAuthApplicationPermissionsChanged(Id, permissions);
		Apply(@event);
		return @event;
	}

	public OAuthApplicationRequirementsChanged SetRequirements(IReadOnlyList<string> requirements)
	{
		var @event = new OAuthApplicationRequirementsChanged(Id, requirements);
		Apply(@event);
		return @event;
	}

	public OAuthApplicationSettingsChanged SetSettings(IReadOnlyDictionary<string, string> settings)
	{
		var @event = new OAuthApplicationSettingsChanged(Id, settings);
		Apply(@event);
		return @event;
	}

	public OAuthApplicationDisplayNamesChanged SetDisplayNames(IReadOnlyDictionary<string, string> displayNames)
	{
		var @event = new OAuthApplicationDisplayNamesChanged(Id, displayNames);
		Apply(@event);
		return @event;
	}

	public OAuthApplicationPropertiesChanged SetProperties(IReadOnlyDictionary<string, object?> properties)
	{
		var @event = new OAuthApplicationPropertiesChanged(Id, properties);
		Apply(@event);
		return @event;
	}

	public OAuthApplicationDeleted Delete()
	{
		var @event = new OAuthApplicationDeleted(Id);
		Apply(@event);
		return @event;
	}

	// Event application methods
	public void Apply(OAuthApplicationCreated @event)
	{
		Id = @event.ApplicationId;
		ClientId = @event.ClientId;
		DisplayName = @event.DisplayName;
		ClientType = @event.ClientType;
		ConsentType = @event.ConsentType;
		ApplicationType = @event.ApplicationType;
		RedirectUris = @event.RedirectUris.ToList();
		PostLogoutRedirectUris = @event.PostLogoutRedirectUris.ToList();
		Permissions = @event.Permissions.ToList();
		Requirements = @event.Requirements.ToList();
	}

	public void Apply(OAuthApplicationDisplayNameChanged @event)
	{
		DisplayName = @event.DisplayName;
	}

	public void Apply(OAuthApplicationClientTypeChanged @event)
	{
		ClientType = @event.ClientType;
	}

	public void Apply(OAuthApplicationConsentTypeChanged @event)
	{
		ConsentType = @event.ConsentType;
	}

	public void Apply(OAuthApplicationRedirectUrisChanged @event)
	{
		RedirectUris = @event.RedirectUris.ToList();
	}

	public void Apply(OAuthApplicationPostLogoutRedirectUrisChanged @event)
	{
		PostLogoutRedirectUris = @event.PostLogoutRedirectUris.ToList();
	}

	public void Apply(OAuthApplicationPermissionsChanged @event)
	{
		Permissions = @event.Permissions.ToList();
	}

	public void Apply(OAuthApplicationRequirementsChanged @event)
	{
		Requirements = @event.Requirements.ToList();
	}

	public void Apply(OAuthApplicationSettingsChanged @event)
	{
		Settings = new Dictionary<string, string>(@event.Settings);
	}

	public void Apply(OAuthApplicationDisplayNamesChanged @event)
	{
		DisplayNames = new Dictionary<string, string>(@event.DisplayNames);
	}

	public void Apply(OAuthApplicationPropertiesChanged @event)
	{
		Properties = new Dictionary<string, object?>(@event.Properties);
	}

	public void Apply(OAuthApplicationDeleted @event)
	{
		IsDeleted = true;
	}
}
