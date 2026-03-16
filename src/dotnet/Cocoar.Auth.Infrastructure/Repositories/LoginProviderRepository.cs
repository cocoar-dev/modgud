using Cocoar.Auth.Application.DTOs.LoginProviders;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Aggregates;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Infrastructure.Persistence;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using ErrorOr;
using Marten;

namespace Cocoar.Auth.Infrastructure.Repositories;

/// <summary>
/// Marten-based repository for login providers.
/// Uses event sourcing for domain data.
/// </summary>
public class LoginProviderRepository : ILoginProviderRepository
{
	private readonly ITenantSessionFactory _sessionFactory;

	public LoginProviderRepository(ITenantSessionFactory sessionFactory)
	{
		_sessionFactory = sessionFactory;
	}

	public async Task<LoginProviderListDto> GetAllAsync(CancellationToken cancellationToken = default)
	{
		await using var session = _sessionFactory.OpenQuerySession();

		var providers = await session.Query<LoginProviderState>()
			.Where(x => !x.IsDeleted)
			.OrderBy(x => x.Name)
			.ToListAsync(cancellationToken);

		var items = providers.Select(MapToDto).ToList();

		return new LoginProviderListDto
		{
			Items = items,
			TotalCount = items.Count
		};
	}

	public async Task<LoginProviderDto?> GetByIdAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		if (!Guid.TryParse(id, out var guid))
		{
			return null;
		}

		await using var session = _sessionFactory.OpenQuerySession();
		var provider = await session.LoadAsync<LoginProviderState>(guid, cancellationToken);

		if (provider is null || provider.IsDeleted)
		{
			return null;
		}

		return MapToDto(provider);
	}

	public async Task<LoginProviderDto?> GetByNameAsync(
		string name,
		CancellationToken cancellationToken = default)
	{
		await using var session = _sessionFactory.OpenQuerySession();

		var provider = await session.Query<LoginProviderState>()
			.FirstOrDefaultAsync(x => x.Name == name && !x.IsDeleted, cancellationToken);

		if (provider is null)
		{
			return null;
		}

		return MapToDto(provider);
	}

	public async Task<ErrorOr<LoginProviderDto>> CreateAsync(
		CreateLoginProviderDto dto,
		CancellationToken cancellationToken = default)
	{
		await using var session = _sessionFactory.OpenSession();

		// Check if login provider name already exists
		var existing = await session.Query<LoginProviderState>()
			.FirstOrDefaultAsync(x => x.Name == dto.Name && !x.IsDeleted, cancellationToken);

		if (existing is not null)
		{
			return LoginProviderErrors.DuplicateName(dto.Name);
		}

		var id = Guid.NewGuid();

		// Create the aggregate and emit the creation event
		var (_, createdEvent) = LoginProviderAggregate.Create(
			id,
			dto.Name,
			dto.DisplayName,
			dto.Description,
			dto.Type,
			dto.Configuration,
			isBuiltIn: false);

		session.Events.StartStream<LoginProviderAggregate>(id, createdEvent);

		await session.SaveChangesAsync(cancellationToken);

		// Reload the state to get the projected values
		var state = await session.LoadAsync<LoginProviderState>(id, cancellationToken);
		return MapToDto(state!);
	}

	public async Task<ErrorOr<LoginProviderDto>> UpdateAsync(
		string id,
		UpdateLoginProviderDto dto,
		CancellationToken cancellationToken = default)
	{
		if (!Guid.TryParse(id, out var guid))
		{
			return LoginProviderErrors.NotFound(id);
		}

		await using var session = _sessionFactory.OpenSession();

		var currentState = await session.LoadAsync<LoginProviderState>(guid, cancellationToken);
		if (currentState is null || currentState.IsDeleted)
		{
			return LoginProviderErrors.NotFound(id);
		}

		var aggregate = await session.Events.AggregateStreamAsync<LoginProviderAggregate>(guid, token: cancellationToken);
		if (aggregate is null)
		{
			return LoginProviderErrors.NotFound(id);
		}

		// Emit events for changed properties
		if (dto.Name is not null && dto.Name != currentState.Name)
		{
			// Check for duplicate name
			var existing = await session.Query<LoginProviderState>()
				.FirstOrDefaultAsync(x => x.Name == dto.Name && !x.IsDeleted && x.Id != guid, cancellationToken);

			if (existing is not null)
			{
				return LoginProviderErrors.DuplicateName(dto.Name);
			}

			var evt = aggregate.SetName(dto.Name);
			session.Events.Append(guid, evt);
		}

		if (dto.DisplayName is not null && dto.DisplayName != currentState.DisplayName)
		{
			var evt = aggregate.SetDisplayName(dto.DisplayName);
			session.Events.Append(guid, evt);
		}

		if (dto.Description is not null && dto.Description != currentState.Description)
		{
			var evt = aggregate.SetDescription(dto.Description);
			session.Events.Append(guid, evt);
		}

		if (dto.Configuration is not null)
		{
			var evt = aggregate.SetConfiguration(dto.Configuration);
			session.Events.Append(guid, evt);
		}

		await session.SaveChangesAsync(cancellationToken);

		// Reload the state to get updated values
		var updatedState = await session.LoadAsync<LoginProviderState>(guid, cancellationToken);
		return MapToDto(updatedState!);
	}

	public async Task<ErrorOr<bool>> DeleteAsync(
		string id,
		CancellationToken cancellationToken = default)
	{
		if (!Guid.TryParse(id, out var guid))
		{
			return LoginProviderErrors.NotFound(id);
		}

		await using var session = _sessionFactory.OpenSession();

		var currentState = await session.LoadAsync<LoginProviderState>(guid, cancellationToken);
		if (currentState is null || currentState.IsDeleted)
		{
			return LoginProviderErrors.NotFound(id);
		}

		if (currentState.IsBuiltIn)
		{
			return LoginProviderErrors.CannotDeleteBuiltIn(currentState.Name);
		}

		var aggregate = await session.Events.AggregateStreamAsync<LoginProviderAggregate>(guid, token: cancellationToken);
		if (aggregate is null || aggregate.IsDeleted)
		{
			return LoginProviderErrors.NotFound(id);
		}

		var deletedEvent = aggregate.Delete();
		session.Events.Append(guid, deletedEvent);

		await session.SaveChangesAsync(cancellationToken);
		return true;
	}

	public async Task EnsureInternalProviderExistsAsync(CancellationToken cancellationToken = default)
	{
		await using var session = _sessionFactory.OpenSession();

		var existing = await session.Query<LoginProviderState>()
			.FirstOrDefaultAsync(x => x.Name == "Internal" && !x.IsDeleted, cancellationToken);

		if (existing is not null)
		{
			return;
		}

		var id = Guid.NewGuid();

		var (_, createdEvent) = LoginProviderAggregate.Create(
			id,
			"Internal",
			"Internal Authentication",
			"Built-in password-based authentication",
			LoginProviderType.Internal,
			new Dictionary<string, string>(),
			isBuiltIn: true);

		session.Events.StartStream<LoginProviderAggregate>(id, createdEvent);
		await session.SaveChangesAsync(cancellationToken);
	}

	private static LoginProviderDto MapToDto(LoginProviderState state)
	{
		return new LoginProviderDto
		{
			Id = state.Id.ToString(),
			Name = state.Name,
			DisplayName = state.DisplayName,
			Description = state.Description,
			Type = state.Type,
			Configuration = state.Configuration,
			IsBuiltIn = state.IsBuiltIn
		};
	}
}
