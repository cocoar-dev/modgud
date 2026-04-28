using Cocoar.Auth.Application.DTOs.LoginProviders;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Domain.Identity.LoginProviders;
using ErrorOr;
using Marten;

namespace Cocoar.Auth.Application.Services;

/// <summary>
/// CRUD service for login provider configuration. Built on the event-sourced
/// <see cref="LoginProviderAggregate"/> + inline <see cref="LoginProviderState"/>
/// projection. The injected <see cref="IDocumentSession"/> is tenant-scoped via
/// <c>TenantedSessionFactory</c>.
/// </summary>
public class LoginProviderService
{
    private readonly IDocumentSession _session;

    public LoginProviderService(IDocumentSession session)
    {
        _session = session;
    }

    public async Task<LoginProviderListDto> GetAllAsync(CancellationToken ct = default)
    {
        var providers = await _session.Query<LoginProviderState>()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var items = providers.Select(MapToDto).ToList();
        return new LoginProviderListDto { Items = items, TotalCount = items.Count };
    }

    public async Task<ErrorOr<LoginProviderDto>> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return LoginProviderErrors.NotFound(id);

        var state = await _session.LoadAsync<LoginProviderState>(guid, ct);
        if (state is null || state.IsDeleted)
            return LoginProviderErrors.NotFound(id);

        return MapToDto(state);
    }

    public async Task<ErrorOr<LoginProviderDto>> CreateAsync(
        CreateLoginProviderDto dto, CancellationToken ct = default)
    {
        var existing = await _session.Query<LoginProviderState>()
            .FirstOrDefaultAsync(x => x.Name == dto.Name && !x.IsDeleted, ct);
        if (existing is not null)
            return LoginProviderErrors.DuplicateName(dto.Name);

        var id = Guid.NewGuid();
        var (_, createdEvent) = LoginProviderAggregate.Create(
            id, dto.Name, dto.DisplayName, dto.Description, dto.Type, dto.Configuration, isBuiltIn: false);
        _session.Events.StartStream<LoginProviderAggregate>(id, createdEvent);

        await _session.SaveChangesAsync(ct);

        var state = await _session.LoadAsync<LoginProviderState>(id, ct);
        return MapToDto(state!);
    }

    public async Task<ErrorOr<LoginProviderDto>> UpdateAsync(
        string id, UpdateLoginProviderDto dto, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return LoginProviderErrors.NotFound(id);

        var state = await _session.LoadAsync<LoginProviderState>(guid, ct);
        if (state is null || state.IsDeleted)
            return LoginProviderErrors.NotFound(id);

        var aggregate = await _session.Events.AggregateStreamAsync<LoginProviderAggregate>(guid, token: ct);
        if (aggregate is null) return LoginProviderErrors.NotFound(id);

        if (dto.Name is not null && dto.Name != state.Name)
        {
            var existing = await _session.Query<LoginProviderState>()
                .FirstOrDefaultAsync(x => x.Name == dto.Name && !x.IsDeleted && x.Id != guid, ct);
            if (existing is not null)
                return LoginProviderErrors.DuplicateName(dto.Name);
            _session.Events.Append(guid, aggregate.SetName(dto.Name));
        }

        if (dto.DisplayName is not null && dto.DisplayName != state.DisplayName)
            _session.Events.Append(guid, aggregate.SetDisplayName(dto.DisplayName));

        if (dto.Description is not null && dto.Description != state.Description)
            _session.Events.Append(guid, aggregate.SetDescription(dto.Description));

        if (dto.Configuration is not null)
            _session.Events.Append(guid, aggregate.SetConfiguration(dto.Configuration));

        await _session.SaveChangesAsync(ct);

        var updated = await _session.LoadAsync<LoginProviderState>(guid, ct);
        return MapToDto(updated!);
    }

    public async Task<ErrorOr<bool>> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return LoginProviderErrors.NotFound(id);

        var state = await _session.LoadAsync<LoginProviderState>(guid, ct);
        if (state is null || state.IsDeleted)
            return LoginProviderErrors.NotFound(id);

        if (state.IsBuiltIn)
            return LoginProviderErrors.CannotDeleteBuiltIn(state.Name);

        var aggregate = await _session.Events.AggregateStreamAsync<LoginProviderAggregate>(guid, token: ct);
        if (aggregate is null || aggregate.IsDeleted)
            return LoginProviderErrors.NotFound(id);

        _session.Events.Append(guid, aggregate.Delete());
        await _session.SaveChangesAsync(ct);
        return true;
    }

    private static LoginProviderDto MapToDto(LoginProviderState s) => new()
    {
        Id = s.Id.ToString(),
        Name = s.Name,
        DisplayName = s.DisplayName,
        Description = s.Description,
        Type = s.Type,
        Configuration = s.Configuration,
        IsBuiltIn = s.IsBuiltIn,
    };
}
