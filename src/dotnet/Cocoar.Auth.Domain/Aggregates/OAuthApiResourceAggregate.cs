using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Domain.Aggregates;

/// <summary>
/// Aggregate for OAuth API Resources.
/// Sensitive data (ApiSecret) is stored separately in OAuthApiResourceSecurityData.
/// API Resources represent protected APIs that accept tokens from this IdP.
/// </summary>
public class OAuthApiResourceAggregate
{
	public Guid Id { get; private set; }
	public string Name { get; private set; } = string.Empty;
	public string? DisplayName { get; private set; }
	public string? Description { get; private set; }
	public bool Enabled { get; private set; } = true;
	public List<string> Scopes { get; private set; } = new();
	public List<string> UserClaims { get; private set; } = new();
	public Dictionary<string, object?> Properties { get; private set; } = new();
	public bool IsDeleted { get; private set; }

	// For Marten event sourcing
	public OAuthApiResourceAggregate() { }

	public static (OAuthApiResourceAggregate, OAuthApiResourceCreated) Create(
		Guid id,
		string name,
		string? displayName,
		string? description,
		bool enabled,
		IReadOnlyList<string> scopes)
	{
		var aggregate = new OAuthApiResourceAggregate();
		var @event = new OAuthApiResourceCreated(
			id,
			name,
			displayName,
			description,
			enabled,
			scopes);

		aggregate.Apply(@event);
		return (aggregate, @event);
	}

	public OAuthApiResourceDisplayNameChanged SetDisplayName(string? displayName)
	{
		var @event = new OAuthApiResourceDisplayNameChanged(Id, displayName);
		Apply(@event);
		return @event;
	}

	public OAuthApiResourceDescriptionChanged SetDescription(string? description)
	{
		var @event = new OAuthApiResourceDescriptionChanged(Id, description);
		Apply(@event);
		return @event;
	}

	public OAuthApiResourceEnabled Enable()
	{
		var @event = new OAuthApiResourceEnabled(Id);
		Apply(@event);
		return @event;
	}

	public OAuthApiResourceDisabled Disable()
	{
		var @event = new OAuthApiResourceDisabled(Id);
		Apply(@event);
		return @event;
	}

	public OAuthApiResourceScopesChanged SetScopes(IReadOnlyList<string> scopes)
	{
		var @event = new OAuthApiResourceScopesChanged(Id, scopes);
		Apply(@event);
		return @event;
	}

	public OAuthApiResourceUserClaimsChanged SetUserClaims(IReadOnlyList<string> userClaims)
	{
		var @event = new OAuthApiResourceUserClaimsChanged(Id, userClaims);
		Apply(@event);
		return @event;
	}

	public OAuthApiResourcePropertiesChanged SetProperties(IReadOnlyDictionary<string, object?> properties)
	{
		var @event = new OAuthApiResourcePropertiesChanged(Id, properties);
		Apply(@event);
		return @event;
	}

	public OAuthApiResourceDeleted Delete()
	{
		var @event = new OAuthApiResourceDeleted(Id);
		Apply(@event);
		return @event;
	}

	// Event application methods
	public void Apply(OAuthApiResourceCreated @event)
	{
		Id = @event.ApiResourceId;
		Name = @event.Name;
		DisplayName = @event.DisplayName;
		Description = @event.Description;
		Enabled = @event.Enabled;
		Scopes = @event.Scopes.ToList();
	}

	public void Apply(OAuthApiResourceDisplayNameChanged @event)
	{
		DisplayName = @event.DisplayName;
	}

	public void Apply(OAuthApiResourceDescriptionChanged @event)
	{
		Description = @event.Description;
	}

	public void Apply(OAuthApiResourceEnabled @event)
	{
		Enabled = true;
	}

	public void Apply(OAuthApiResourceDisabled @event)
	{
		Enabled = false;
	}

	public void Apply(OAuthApiResourceScopesChanged @event)
	{
		Scopes = @event.Scopes.ToList();
	}

	public void Apply(OAuthApiResourceUserClaimsChanged @event)
	{
		UserClaims = @event.UserClaims.ToList();
	}

	public void Apply(OAuthApiResourcePropertiesChanged @event)
	{
		Properties = new Dictionary<string, object?>(@event.Properties);
	}

	public void Apply(OAuthApiResourceDeleted @event)
	{
		IsDeleted = true;
	}
}
