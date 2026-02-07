using Cocoar.Auth.Domain.Events;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections;

/// <summary>
/// State projection for OAuth scopes.
/// This is an inline projection for immediate consistency in validation and lookups.
/// Naming Convention: *State = Inline projection for validation/identity
/// </summary>
public class OAuthScopeState
{
	public Guid Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public string? DisplayName { get; set; }
	public string? Description { get; set; }
	public List<string> Resources { get; set; } = new();
	public Dictionary<string, string> DisplayNames { get; set; } = new();
	public Dictionary<string, string> Descriptions { get; set; } = new();
	public Dictionary<string, object?> Properties { get; set; } = new();
	public bool IsDeleted { get; set; }
}

/// <summary>
/// Inline projection that builds OAuthScopeState from events.
/// </summary>
public class OAuthScopeStateProjection : SingleStreamProjection<OAuthScopeState, Guid>
{
	public OAuthScopeState Create(OAuthScopeCreated @event)
	{
		return new OAuthScopeState
		{
			Id = @event.ScopeId,
			Name = @event.Name,
			DisplayName = @event.DisplayName,
			Description = @event.Description,
			Resources = @event.Resources.ToList()
		};
	}

	public void Apply(OAuthScopeDisplayNameChanged @event, OAuthScopeState state)
	{
		state.DisplayName = @event.DisplayName;
	}

	public void Apply(OAuthScopeDescriptionChanged @event, OAuthScopeState state)
	{
		state.Description = @event.Description;
	}

	public void Apply(OAuthScopeResourcesChanged @event, OAuthScopeState state)
	{
		state.Resources = @event.Resources.ToList();
	}

	public void Apply(OAuthScopeDisplayNamesChanged @event, OAuthScopeState state)
	{
		state.DisplayNames = new Dictionary<string, string>(@event.DisplayNames);
	}

	public void Apply(OAuthScopeDescriptionsChanged @event, OAuthScopeState state)
	{
		state.Descriptions = new Dictionary<string, string>(@event.Descriptions);
	}

	public void Apply(OAuthScopePropertiesChanged @event, OAuthScopeState state)
	{
		state.Properties = new Dictionary<string, object?>(@event.Properties);
	}

	public void Apply(OAuthScopeDeleted @event, OAuthScopeState state)
	{
		state.IsDeleted = true;
	}
}
