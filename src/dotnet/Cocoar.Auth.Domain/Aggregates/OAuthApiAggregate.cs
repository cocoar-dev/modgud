using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Domain.Aggregates;

/// <summary>
/// Aggregate for OAuth APIs.
/// Sensitive data (ApiSecret) is stored separately in OAuthApiSecurityData.
/// APIs represent protected APIs that accept tokens from this IdP.
/// </summary>
public class OAuthApiAggregate
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
	public OAuthApiAggregate() { }

	public static (OAuthApiAggregate, OAuthApiCreated) Create(
		Guid id,
		string name,
		string? displayName,
		string? description,
		bool enabled,
		IReadOnlyList<string> scopes)
	{
		var aggregate = new OAuthApiAggregate();
		var @event = new OAuthApiCreated(
			id,
			name,
			displayName,
			description,
			enabled,
			scopes);

		aggregate.Apply(@event);
		return (aggregate, @event);
	}

	public OAuthApiDisplayNameChanged SetDisplayName(string? displayName)
	{
		var @event = new OAuthApiDisplayNameChanged(Id, displayName);
		Apply(@event);
		return @event;
	}

	public OAuthApiDescriptionChanged SetDescription(string? description)
	{
		var @event = new OAuthApiDescriptionChanged(Id, description);
		Apply(@event);
		return @event;
	}

	public OAuthApiEnabled Enable()
	{
		var @event = new OAuthApiEnabled(Id);
		Apply(@event);
		return @event;
	}

	public OAuthApiDisabled Disable()
	{
		var @event = new OAuthApiDisabled(Id);
		Apply(@event);
		return @event;
	}

	public OAuthApiScopesChanged SetScopes(IReadOnlyList<string> scopes)
	{
		var @event = new OAuthApiScopesChanged(Id, scopes);
		Apply(@event);
		return @event;
	}

	public OAuthApiUserClaimsChanged SetUserClaims(IReadOnlyList<string> userClaims)
	{
		var @event = new OAuthApiUserClaimsChanged(Id, userClaims);
		Apply(@event);
		return @event;
	}

	public OAuthApiPropertiesChanged SetProperties(IReadOnlyDictionary<string, object?> properties)
	{
		var @event = new OAuthApiPropertiesChanged(Id, properties);
		Apply(@event);
		return @event;
	}

	public OAuthApiDeleted Delete()
	{
		var @event = new OAuthApiDeleted(Id);
		Apply(@event);
		return @event;
	}

	// Event application methods
	public void Apply(OAuthApiCreated @event)
	{
		Id = @event.ApiId;
		Name = @event.Name;
		DisplayName = @event.DisplayName;
		Description = @event.Description;
		Enabled = @event.Enabled;
		Scopes = @event.Scopes.ToList();
	}

	public void Apply(OAuthApiDisplayNameChanged @event)
	{
		DisplayName = @event.DisplayName;
	}

	public void Apply(OAuthApiDescriptionChanged @event)
	{
		Description = @event.Description;
	}

	public void Apply(OAuthApiEnabled @event)
	{
		Enabled = true;
	}

	public void Apply(OAuthApiDisabled @event)
	{
		Enabled = false;
	}

	public void Apply(OAuthApiScopesChanged @event)
	{
		Scopes = @event.Scopes.ToList();
	}

	public void Apply(OAuthApiUserClaimsChanged @event)
	{
		UserClaims = @event.UserClaims.ToList();
	}

	public void Apply(OAuthApiPropertiesChanged @event)
	{
		Properties = new Dictionary<string, object?>(@event.Properties);
	}

	public void Apply(OAuthApiDeleted @event)
	{
		IsDeleted = true;
	}
}
