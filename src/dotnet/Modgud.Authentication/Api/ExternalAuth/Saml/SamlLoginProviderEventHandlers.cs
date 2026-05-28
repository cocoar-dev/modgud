using Marten;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

// Wolverine discovers handler classes by `*Handler` convention. SAML
// handlers run in parallel to the OIDC handlers in
// LoginProviderEventHandlers.cs — the event stream is shared across
// provider types, each handler short-circuits if it's not the type it
// cares about. Type discrimination lives in SamlLoginProviderReRegister
// so the policy is one place.

public class SamlLoginProviderOnAddedHandler(
    IQuerySession session,
    DynamicSamlSchemeManager manager)
{
    public Task Handle(LoginProviderAddedEvent @event, CancellationToken ct) =>
        SamlLoginProviderReRegister.Run(@event.Id, session, manager, ct);
}

public class SamlLoginProviderOnUpdatedHandler(
    IQuerySession session,
    DynamicSamlSchemeManager manager)
{
    public Task Handle(LoginProviderUpdatedEvent @event, CancellationToken ct) =>
        SamlLoginProviderReRegister.Run(@event.Id, session, manager, ct);
}

public class SamlLoginProviderOnEnabledHandler(
    IQuerySession session,
    DynamicSamlSchemeManager manager)
{
    public Task Handle(LoginProviderEnabledEvent @event, CancellationToken ct) =>
        SamlLoginProviderReRegister.Run(@event.Id, session, manager, ct);
}

public class SamlLoginProviderOnDisabledHandler(DynamicSamlSchemeManager manager)
{
    public Task Handle(LoginProviderDisabledEvent @event, CancellationToken ct) =>
        manager.UnregisterAsync(@event.Id);
}

public class SamlLoginProviderOnSecretRotatedHandler(
    IQuerySession session,
    DynamicSamlSchemeManager manager)
{
    // SAML has no ClientSecret as such, but rotating the SP signing cert (a
    // future event in this slice) and any other config change route through
    // the generic Updated event. The SecretRotated event is OIDC-shaped; we
    // still handle it harmlessly here in case a SAML LoginProvider record
    // ever emits one (e.g. via a generic admin-rotate-all UI).
    public Task Handle(LoginProviderSecretRotatedEvent @event, CancellationToken ct) =>
        SamlLoginProviderReRegister.Run(@event.Id, session, manager, ct);
}

public class SamlLoginProviderOnDeletedHandler(DynamicSamlSchemeManager manager)
{
    public Task Handle(LoginProviderDeletedEvent @event, CancellationToken ct) =>
        manager.UnregisterAsync(@event.Id);
}

internal static class SamlLoginProviderReRegister
{
    public static async Task Run(Guid id,
        IQuerySession session,
        DynamicSamlSchemeManager manager,
        CancellationToken ct)
    {
        var config = await session.LoadAsync<LoginProvider>(id, ct);
        if (config is null)
        {
            // Missing record => evict cache; do this unconditionally so a
            // deleted SAML provider whose record disappeared still drops
            // out of the cache.
            await manager.UnregisterAsync(id);
            return;
        }

        // Type-discriminator gate. OIDC / Internal / Ldap / Kerberos events
        // shouldn't register against the SAML manager. But: if a stale SAML
        // entry exists in the cache under this id (rare — only reachable via
        // direct event-stream surgery or a future force-recreate code path
        // that recycles the Guid with a different Type), evict it on the way
        // out so subsequent SAML lookups don't hit the wrong-protocol entry.
        if (config.Type != LoginProviderType.Saml)
        {
            await manager.UnregisterAsync(id);
            return;
        }

        // Wolverine event handlers run in a background message pump where
        // RealmMiddleware hasn't set the TenantContext for us. The session
        // itself is tenant-scoped (via TenantedSessionFactory looking at
        // the message envelope) — pull its TenantId and enter the context
        // so manager.RegisterAsync can stamp the cache entry with the
        // right realm slug.
        var sessionTenantId = session.TenantId;
        if (!string.IsNullOrEmpty(sessionTenantId)
            && string.IsNullOrEmpty(TenantContext.CurrentOrNull))
        {
            using var _ = TenantContext.Enter(sessionTenantId);
            await manager.RegisterAsync(config);
            return;
        }

        await manager.RegisterAsync(config);
    }
}
