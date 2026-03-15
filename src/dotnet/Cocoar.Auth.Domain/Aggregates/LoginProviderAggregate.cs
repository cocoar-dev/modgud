using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Domain.Aggregates;

/// <summary>
/// Event-sourced aggregate for login provider data.
/// Represents an authentication method (Internal password, OpenID Connect external providers).
/// </summary>
public class LoginProviderAggregate
{
	/// <summary>
	/// The unique identifier for this login provider.
	/// </summary>
	public Guid Id { get; private set; }

	/// <summary>
	/// The unique name of the login provider.
	/// </summary>
	public string Name { get; private set; } = string.Empty;

	/// <summary>
	/// The display name of the login provider.
	/// </summary>
	public string? DisplayName { get; private set; }

	/// <summary>
	/// A description of the login provider.
	/// </summary>
	public string? Description { get; private set; }

	/// <summary>
	/// The type of login provider (Internal, OpenIdConnect).
	/// </summary>
	public LoginProviderType Type { get; private set; }

	/// <summary>
	/// Configuration settings for this login provider (e.g., Authority, ClientId for OIDC).
	/// </summary>
	public Dictionary<string, string> Configuration { get; private set; } = new();

	/// <summary>
	/// Whether this login provider is built-in and cannot be deleted.
	/// </summary>
	public bool IsBuiltIn { get; private set; }

	/// <summary>
	/// Whether this login provider has been deleted (soft delete).
	/// </summary>
	public bool IsDeleted { get; private set; }

	// For Marten event sourcing
	public LoginProviderAggregate() { }

	public static (LoginProviderAggregate, LoginProviderCreated) Create(
		Guid id,
		string name,
		string? displayName,
		string? description,
		LoginProviderType type,
		Dictionary<string, string> configuration,
		bool isBuiltIn)
	{
		var aggregate = new LoginProviderAggregate();
		var @event = new LoginProviderCreated(
			id,
			name,
			displayName,
			description,
			type,
			configuration,
			isBuiltIn);

		aggregate.Apply(@event);
		return (aggregate, @event);
	}

	public LoginProviderNameChanged SetName(string name)
	{
		var @event = new LoginProviderNameChanged(Id, name);
		Apply(@event);
		return @event;
	}

	public LoginProviderDisplayNameChanged SetDisplayName(string? displayName)
	{
		var @event = new LoginProviderDisplayNameChanged(Id, displayName);
		Apply(@event);
		return @event;
	}

	public LoginProviderDescriptionChanged SetDescription(string? description)
	{
		var @event = new LoginProviderDescriptionChanged(Id, description);
		Apply(@event);
		return @event;
	}

	public LoginProviderConfigurationChanged SetConfiguration(Dictionary<string, string> configuration)
	{
		var @event = new LoginProviderConfigurationChanged(Id, configuration);
		Apply(@event);
		return @event;
	}

	public LoginProviderDeleted Delete()
	{
		var @event = new LoginProviderDeleted(Id);
		Apply(@event);
		return @event;
	}

	// ═══════════════════════════════════════════════════════════════════════
	// EVENT APPLICATION METHODS
	// These methods are called by Marten when replaying events to build state.
	// ═══════════════════════════════════════════════════════════════════════

	public void Apply(LoginProviderCreated @event)
	{
		Id = @event.LoginProviderId;
		Name = @event.Name;
		DisplayName = @event.DisplayName;
		Description = @event.Description;
		Type = @event.Type;
		Configuration = new Dictionary<string, string>(@event.Configuration);
		IsBuiltIn = @event.IsBuiltIn;
	}

	public void Apply(LoginProviderNameChanged @event)
	{
		Name = @event.NewName;
	}

	public void Apply(LoginProviderDisplayNameChanged @event)
	{
		DisplayName = @event.NewDisplayName;
	}

	public void Apply(LoginProviderDescriptionChanged @event)
	{
		Description = @event.NewDescription;
	}

	public void Apply(LoginProviderConfigurationChanged @event)
	{
		Configuration = new Dictionary<string, string>(@event.NewConfiguration);
	}

	public void Apply(LoginProviderDeleted @event)
	{
		IsDeleted = true;
	}
}
