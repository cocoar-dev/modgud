using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Domain.Aggregates;

/// <summary>
/// Aggregate for OAuth scopes.
/// Scopes don't contain sensitive data, so everything is event-sourced.
/// </summary>
public class OAuthScopeAggregate
{
	public Guid Id { get; private set; }
	public string Name { get; private set; } = string.Empty;
	public string? DisplayName { get; private set; }
	public string? Description { get; private set; }
	public List<string> Resources { get; private set; } = new();
	public Dictionary<string, string> DisplayNames { get; private set; } = new();
	public Dictionary<string, string> Descriptions { get; private set; } = new();
	public Dictionary<string, object?> Properties { get; private set; } = new();
	public bool IsDeleted { get; private set; }

	// For Marten event sourcing
	public OAuthScopeAggregate() { }

	public static (OAuthScopeAggregate, OAuthScopeCreated) Create(
		Guid id,
		string name,
		string? displayName,
		string? description,
		IReadOnlyList<string> resources)
	{
		var aggregate = new OAuthScopeAggregate();
		var @event = new OAuthScopeCreated(id, name, displayName, description, resources);
		aggregate.Apply(@event);
		return (aggregate, @event);
	}

	public OAuthScopeDisplayNameChanged SetDisplayName(string? displayName)
	{
		var @event = new OAuthScopeDisplayNameChanged(Id, displayName);
		Apply(@event);
		return @event;
	}

	public OAuthScopeDescriptionChanged SetDescription(string? description)
	{
		var @event = new OAuthScopeDescriptionChanged(Id, description);
		Apply(@event);
		return @event;
	}

	public OAuthScopeResourcesChanged SetResources(IReadOnlyList<string> resources)
	{
		var @event = new OAuthScopeResourcesChanged(Id, resources);
		Apply(@event);
		return @event;
	}

	public OAuthScopeDisplayNamesChanged SetDisplayNames(IReadOnlyDictionary<string, string> displayNames)
	{
		var @event = new OAuthScopeDisplayNamesChanged(Id, displayNames);
		Apply(@event);
		return @event;
	}

	public OAuthScopeDescriptionsChanged SetDescriptions(IReadOnlyDictionary<string, string> descriptions)
	{
		var @event = new OAuthScopeDescriptionsChanged(Id, descriptions);
		Apply(@event);
		return @event;
	}

	public OAuthScopePropertiesChanged SetProperties(IReadOnlyDictionary<string, object?> properties)
	{
		var @event = new OAuthScopePropertiesChanged(Id, properties);
		Apply(@event);
		return @event;
	}

	public OAuthScopeDeleted Delete()
	{
		var @event = new OAuthScopeDeleted(Id);
		Apply(@event);
		return @event;
	}

	// Event application methods
	public void Apply(OAuthScopeCreated @event)
	{
		Id = @event.ScopeId;
		Name = @event.Name;
		DisplayName = @event.DisplayName;
		Description = @event.Description;
		Resources = @event.Resources.ToList();
	}

	public void Apply(OAuthScopeDisplayNameChanged @event)
	{
		DisplayName = @event.DisplayName;
	}

	public void Apply(OAuthScopeDescriptionChanged @event)
	{
		Description = @event.Description;
	}

	public void Apply(OAuthScopeResourcesChanged @event)
	{
		Resources = @event.Resources.ToList();
	}

	public void Apply(OAuthScopeDisplayNamesChanged @event)
	{
		DisplayNames = new Dictionary<string, string>(@event.DisplayNames);
	}

	public void Apply(OAuthScopeDescriptionsChanged @event)
	{
		Descriptions = new Dictionary<string, string>(@event.Descriptions);
	}

	public void Apply(OAuthScopePropertiesChanged @event)
	{
		Properties = new Dictionary<string, object?>(@event.Properties);
	}

	public void Apply(OAuthScopeDeleted @event)
	{
		IsDeleted = true;
	}
}
