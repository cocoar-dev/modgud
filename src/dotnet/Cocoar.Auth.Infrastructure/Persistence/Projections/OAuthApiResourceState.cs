using Cocoar.Auth.Domain.Events;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections;

/// <summary>
/// State projection for OAuth API resources.
/// This is an inline projection for immediate consistency in validation and lookups.
/// Naming Convention: *State = Inline projection for validation/identity
/// </summary>
public class OAuthApiResourceState
{
	public Guid Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public string? DisplayName { get; set; }
	public string? Description { get; set; }
	public bool Enabled { get; set; } = true;
	public List<string> Scopes { get; set; } = new();
	public List<string> UserClaims { get; set; } = new();
	public Dictionary<string, object?> Properties { get; set; } = new();
	public bool IsDeleted { get; set; }
}

/// <summary>
/// Inline projection that builds OAuthApiResourceState from events.
/// </summary>
public class OAuthApiResourceStateProjection : SingleStreamProjection<OAuthApiResourceState, Guid>
{
	public OAuthApiResourceState Create(OAuthApiResourceCreated @event)
	{
		return new OAuthApiResourceState
		{
			Id = @event.ApiResourceId,
			Name = @event.Name,
			DisplayName = @event.DisplayName,
			Description = @event.Description,
			Enabled = @event.Enabled,
			Scopes = @event.Scopes.ToList()
		};
	}

	public void Apply(OAuthApiResourceDisplayNameChanged @event, OAuthApiResourceState state)
	{
		state.DisplayName = @event.DisplayName;
	}

	public void Apply(OAuthApiResourceDescriptionChanged @event, OAuthApiResourceState state)
	{
		state.Description = @event.Description;
	}

	public void Apply(OAuthApiResourceEnabled @event, OAuthApiResourceState state)
	{
		state.Enabled = true;
	}

	public void Apply(OAuthApiResourceDisabled @event, OAuthApiResourceState state)
	{
		state.Enabled = false;
	}

	public void Apply(OAuthApiResourceScopesChanged @event, OAuthApiResourceState state)
	{
		state.Scopes = @event.Scopes.ToList();
	}

	public void Apply(OAuthApiResourceUserClaimsChanged @event, OAuthApiResourceState state)
	{
		state.UserClaims = @event.UserClaims.ToList();
	}

	public void Apply(OAuthApiResourcePropertiesChanged @event, OAuthApiResourceState state)
	{
		state.Properties = new Dictionary<string, object?>(@event.Properties);
	}

	public void Apply(OAuthApiResourceDeleted @event, OAuthApiResourceState state)
	{
		state.IsDeleted = true;
	}
}
