using Marten;
using Cocoar.Auth.Authentication.Domain.LoginProviders;
using Cocoar.Auth.Authentication.Domain.LoginProviders.Events;

namespace Cocoar.Auth.Authentication.Api.ExternalAuth;

// Wolverine discovers handler classes by name/convention (`*Handler` with a
// `Handle` method). One class per event keeps discovery explicit and matches
// the pattern used by AutoMembershipSync* in the Groups feature.
//
// Phase 1 note: handlers fire for all LoginProvider events including those of
// Internal-typed providers. The DynamicOidcSchemeManager filters them out via
// the unknown-flavor early-return; Phase 2 introduces an explicit
// Type == Oidc guard up front.

public class LoginProviderOnAddedHandler(
    IQuerySession session,
    DynamicOidcSchemeManager manager)
{
    public Task Handle(LoginProviderAddedEvent @event, CancellationToken ct) =>
        LoginProviderReRegister.Run(@event.Id, session, manager, ct);
}

public class LoginProviderOnUpdatedHandler(
    IQuerySession session,
    DynamicOidcSchemeManager manager)
{
    public Task Handle(LoginProviderUpdatedEvent @event, CancellationToken ct) =>
        LoginProviderReRegister.Run(@event.Id, session, manager, ct);
}

public class LoginProviderOnEnabledHandler(
    IQuerySession session,
    DynamicOidcSchemeManager manager)
{
    public Task Handle(LoginProviderEnabledEvent @event, CancellationToken ct) =>
        LoginProviderReRegister.Run(@event.Id, session, manager, ct);
}

public class LoginProviderOnDisabledHandler(DynamicOidcSchemeManager manager)
{
    public Task Handle(LoginProviderDisabledEvent @event, CancellationToken ct) =>
        manager.UnregisterAsync(@event.Id);
}

public class LoginProviderOnSecretRotatedHandler(
    IQuerySession session,
    DynamicOidcSchemeManager manager)
{
    public Task Handle(LoginProviderSecretRotatedEvent @event, CancellationToken ct) =>
        LoginProviderReRegister.Run(@event.Id, session, manager, ct);
}

public class LoginProviderOnDeletedHandler(DynamicOidcSchemeManager manager)
{
    public Task Handle(LoginProviderDeletedEvent @event, CancellationToken ct) =>
        manager.UnregisterAsync(@event.Id);
}

internal static class LoginProviderReRegister
{
    public static async Task Run(Guid id,
        IQuerySession session,
        DynamicOidcSchemeManager manager,
        CancellationToken ct)
    {
        var config = await session.LoadAsync<LoginProvider>(id, ct);
        if (config is null)
        {
            await manager.UnregisterAsync(id);
            return;
        }
        await manager.RegisterAsync(config);
    }
}
