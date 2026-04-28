using Marten;
using TimeToDo.Authentication.Domain.ExternalAuth;
using TimeToDo.Authentication.Domain.ExternalAuth.Events;

namespace TimeToDo.Authentication.Api.ExternalAuth;

// Wolverine discovers handler classes by name/convention (`*Handler` with a
// `Handle` method). One class per event keeps discovery explicit and matches
// the pattern used by AutoMembershipSync* in the Groups feature.

public class IdpConfigOnAddedHandler(
    IQuerySession session,
    DynamicOidcSchemeManager manager)
{
    public Task Handle(IdpConfigAddedEvent @event, CancellationToken ct) =>
        IdpConfigReRegister.Run(@event.Id, session, manager, ct);
}

public class IdpConfigOnUpdatedHandler(
    IQuerySession session,
    DynamicOidcSchemeManager manager)
{
    public Task Handle(IdpConfigUpdatedEvent @event, CancellationToken ct) =>
        IdpConfigReRegister.Run(@event.Id, session, manager, ct);
}

public class IdpConfigOnEnabledHandler(
    IQuerySession session,
    DynamicOidcSchemeManager manager)
{
    public Task Handle(IdpConfigEnabledEvent @event, CancellationToken ct) =>
        IdpConfigReRegister.Run(@event.Id, session, manager, ct);
}

public class IdpConfigOnDisabledHandler(DynamicOidcSchemeManager manager)
{
    public Task Handle(IdpConfigDisabledEvent @event, CancellationToken ct) =>
        manager.UnregisterAsync(@event.Id);
}

public class IdpConfigOnSecretRotatedHandler(
    IQuerySession session,
    DynamicOidcSchemeManager manager)
{
    public Task Handle(IdpConfigSecretRotatedEvent @event, CancellationToken ct) =>
        IdpConfigReRegister.Run(@event.Id, session, manager, ct);
}

public class IdpConfigOnDeletedHandler(DynamicOidcSchemeManager manager)
{
    public Task Handle(IdpConfigDeletedEvent @event, CancellationToken ct) =>
        manager.UnregisterAsync(@event.Id);
}

internal static class IdpConfigReRegister
{
    public static async Task Run(Guid id,
        IQuerySession session,
        DynamicOidcSchemeManager manager,
        CancellationToken ct)
    {
        var config = await session.LoadAsync<IdpConfig>(id, ct);
        if (config is null)
        {
            await manager.UnregisterAsync(id);
            return;
        }
        await manager.RegisterAsync(config);
    }
}
