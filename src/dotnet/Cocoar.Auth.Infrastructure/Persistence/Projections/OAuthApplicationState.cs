using System.Text.Json.Serialization;
using Cocoar.Auth.Domain.Events;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections;

/// <summary>
/// State projection for OAuth applications.
/// This is an inline projection for immediate consistency in validation and lookups.
/// Naming Convention: *State = Inline projection for validation/identity
/// </summary>
public class OAuthApplicationState
{
	public Guid Id { get; set; }
	public string ClientId { get; set; } = string.Empty;
	public string? DisplayName { get; set; }
	public string? ClientType { get; set; }
	public string? ConsentType { get; set; }
	public string? ApplicationType { get; set; }
	public List<string> RedirectUris { get; set; } = new();
	public List<string> PostLogoutRedirectUris { get; set; } = new();
	public List<string> Permissions { get; set; } = new();
	public List<string> Requirements { get; set; } = new();
	public Dictionary<string, string> Settings { get; set; } = new();
	public Dictionary<string, string> DisplayNames { get; set; } = new();
	public Dictionary<string, object?> Properties { get; set; } = new();
	public bool IsDeleted { get; set; }

	/// <summary>
	/// Temporary storage for client secret during creation/validation flow.
	/// Not persisted - actual secret is stored in OAuthApplicationSecurityData.
	/// </summary>
	[JsonIgnore]
	[Newtonsoft.Json.JsonIgnore]
	public string? PendingClientSecret { get; set; }

	/// <summary>
	/// Temporary storage for JSON Web Key Set during creation/validation flow.
	/// Not persisted - actual key set is stored in OAuthApplicationSecurityData.
	/// </summary>
	[JsonIgnore]
	[Newtonsoft.Json.JsonIgnore]
	public string? PendingJsonWebKeySet { get; set; }
}

/// <summary>
/// Inline projection that builds OAuthApplicationState from events.
/// </summary>
public class OAuthApplicationStateProjection : SingleStreamProjection<OAuthApplicationState, Guid>
{
	public OAuthApplicationState Create(OAuthApplicationCreated @event)
	{
		return new OAuthApplicationState
		{
			Id = @event.ApplicationId,
			ClientId = @event.ClientId,
			DisplayName = @event.DisplayName,
			ClientType = @event.ClientType,
			ConsentType = @event.ConsentType,
			ApplicationType = @event.ApplicationType,
			RedirectUris = @event.RedirectUris.ToList(),
			PostLogoutRedirectUris = @event.PostLogoutRedirectUris.ToList(),
			Permissions = @event.Permissions.ToList(),
			Requirements = @event.Requirements.ToList()
		};
	}

	public void Apply(OAuthApplicationDisplayNameChanged @event, OAuthApplicationState state)
	{
		state.DisplayName = @event.DisplayName;
	}

	public void Apply(OAuthApplicationClientTypeChanged @event, OAuthApplicationState state)
	{
		state.ClientType = @event.ClientType;
	}

	public void Apply(OAuthApplicationConsentTypeChanged @event, OAuthApplicationState state)
	{
		state.ConsentType = @event.ConsentType;
	}

	public void Apply(OAuthApplicationRedirectUrisChanged @event, OAuthApplicationState state)
	{
		state.RedirectUris = @event.RedirectUris.ToList();
	}

	public void Apply(OAuthApplicationPostLogoutRedirectUrisChanged @event, OAuthApplicationState state)
	{
		state.PostLogoutRedirectUris = @event.PostLogoutRedirectUris.ToList();
	}

	public void Apply(OAuthApplicationPermissionsChanged @event, OAuthApplicationState state)
	{
		state.Permissions = @event.Permissions.ToList();
	}

	public void Apply(OAuthApplicationRequirementsChanged @event, OAuthApplicationState state)
	{
		state.Requirements = @event.Requirements.ToList();
	}

	public void Apply(OAuthApplicationSettingsChanged @event, OAuthApplicationState state)
	{
		state.Settings = new Dictionary<string, string>(@event.Settings);
	}

	public void Apply(OAuthApplicationDisplayNamesChanged @event, OAuthApplicationState state)
	{
		state.DisplayNames = new Dictionary<string, string>(@event.DisplayNames);
	}

	public void Apply(OAuthApplicationPropertiesChanged @event, OAuthApplicationState state)
	{
		state.Properties = new Dictionary<string, object?>(@event.Properties);
	}

	public void Apply(OAuthApplicationDeleted @event, OAuthApplicationState state)
	{
		state.IsDeleted = true;
	}
}
