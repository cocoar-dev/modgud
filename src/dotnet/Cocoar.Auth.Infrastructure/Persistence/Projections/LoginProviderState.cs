using Cocoar.Auth.Domain.Events;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections;

// ═══════════════════════════════════════════════════════════════════════════
// INLINE STATE PROJECTION: NORMALIZED LOGIN PROVIDER STATE
// ═══════════════════════════════════════════════════════════════════════════
// Naming Convention: *State = Inline projection, single source of truth
// Use for: validation, uniqueness checks, login provider lookups
// DO NOT use for: API responses, UI display (use async projections instead)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Normalized state model for login provider data, projected from the event stream.
/// This provides fast query access to login provider information for validation.
/// </summary>
public class LoginProviderState
{
	/// <summary>
	/// The unique identifier for this login provider.
	/// </summary>
	public Guid Id { get; set; }

	/// <summary>
	/// The unique name of the login provider.
	/// </summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// The display name of the login provider.
	/// </summary>
	public string? DisplayName { get; set; }

	/// <summary>
	/// A description of the login provider.
	/// </summary>
	public string? Description { get; set; }

	/// <summary>
	/// The type of login provider (Internal, OpenIdConnect).
	/// </summary>
	public LoginProviderType Type { get; set; }

	/// <summary>
	/// Configuration settings for this login provider.
	/// </summary>
	public Dictionary<string, string> Configuration { get; set; } = new();

	/// <summary>
	/// Whether this login provider is built-in and cannot be deleted.
	/// </summary>
	public bool IsBuiltIn { get; set; }

	/// <summary>
	/// Whether this login provider has been deleted (soft delete).
	/// </summary>
	public bool IsDeleted { get; set; }
}

/// <summary>
/// Inline projection that builds LoginProviderState from events.
/// </summary>
public class LoginProviderStateProjection : SingleStreamProjection<LoginProviderState, Guid>
{
	public LoginProviderState Create(LoginProviderCreated @event)
	{
		return new LoginProviderState
		{
			Id = @event.LoginProviderId,
			Name = @event.Name,
			DisplayName = @event.DisplayName,
			Description = @event.Description,
			Type = @event.Type,
			Configuration = new Dictionary<string, string>(@event.Configuration),
			IsBuiltIn = @event.IsBuiltIn
		};
	}

	public void Apply(LoginProviderNameChanged @event, LoginProviderState state)
	{
		state.Name = @event.NewName;
	}

	public void Apply(LoginProviderDisplayNameChanged @event, LoginProviderState state)
	{
		state.DisplayName = @event.NewDisplayName;
	}

	public void Apply(LoginProviderDescriptionChanged @event, LoginProviderState state)
	{
		state.Description = @event.NewDescription;
	}

	public void Apply(LoginProviderConfigurationChanged @event, LoginProviderState state)
	{
		state.Configuration = new Dictionary<string, string>(@event.NewConfiguration);
	}

	public void Apply(LoginProviderDeleted @event, LoginProviderState state)
	{
		state.IsDeleted = true;
	}
}
