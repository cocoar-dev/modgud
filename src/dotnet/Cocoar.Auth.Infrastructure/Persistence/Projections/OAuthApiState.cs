using Cocoar.Auth.Domain.Events;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections;

/// <summary>
/// State projection for OAuth APIs.
/// This is an inline projection for immediate consistency in validation and lookups.
/// Naming Convention: *State = Inline projection for validation/identity
/// </summary>
public class OAuthApiState
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
/// Inline projection that builds OAuthApiState from events.
/// </summary>
public class OAuthApiStateProjection : SingleStreamProjection<OAuthApiState, Guid>
{
	public OAuthApiState Create(OAuthApiCreated @event)
	{
		return new OAuthApiState
		{
			Id = @event.ApiId,
			Name = @event.Name,
			DisplayName = @event.DisplayName,
			Description = @event.Description,
			Enabled = @event.Enabled,
			Scopes = @event.Scopes.ToList()
		};
	}

	public void Apply(OAuthApiDisplayNameChanged @event, OAuthApiState state)
	{
		state.DisplayName = @event.DisplayName;
	}

	public void Apply(OAuthApiDescriptionChanged @event, OAuthApiState state)
	{
		state.Description = @event.Description;
	}

	public void Apply(OAuthApiEnabled @event, OAuthApiState state)
	{
		state.Enabled = true;
	}

	public void Apply(OAuthApiDisabled @event, OAuthApiState state)
	{
		state.Enabled = false;
	}

	public void Apply(OAuthApiScopesChanged @event, OAuthApiState state)
	{
		state.Scopes = @event.Scopes.ToList();
	}

	public void Apply(OAuthApiUserClaimsChanged @event, OAuthApiState state)
	{
		state.UserClaims = @event.UserClaims.ToList();
	}

	public void Apply(OAuthApiPropertiesChanged @event, OAuthApiState state)
	{
		state.Properties = new Dictionary<string, object?>(@event.Properties);
	}

	public void Apply(OAuthApiDeleted @event, OAuthApiState state)
	{
		state.IsDeleted = true;
	}
}
