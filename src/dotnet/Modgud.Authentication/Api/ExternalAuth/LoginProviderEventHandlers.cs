using Marten;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Api.ExternalAuth;

// Wolverine discovers handler classes by name/convention (`*Handler` with a
// `Handle` method). One class per event keeps discovery explicit and matches
// the pattern used by AutoMembershipSync* in the Groups feature.
//
// Phase 2: handlers still fire for every LoginProvider event regardless of
// type (the event stream itself is shared) but the LoginProviderReRegister
// helper short-circuits non-Oidc providers before touching the scheme manager.
// An Internal LoginProvider being added/updated/enabled must NOT cause OIDC
// scheme work. SAML events are handled by SamlLoginProviderEventHandlers;
// LDAP/Kerberos remain unsupported.

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

        // Type-discriminator gate. Every non-OIDC event skips this OIDC
        // scheme-manager path — SAML has its own parallel handlers, and the
        // manager defends itself too. Pre-filtering keeps logs clean. The
        // unregister-on-missing branch above is unconditional on purpose: a
        // deleted Oidc provider whose record vanished should still drop its
        // scheme.
        if (config.Type != LoginProviderType.Oidc) return;

        // Wolverine event handlers run in a background message pump where
        // RealmMiddleware hasn't set the TenantContext. The session itself is
        // tenant-scoped (TenantedSessionFactory reads the message envelope), so
        // pull its TenantId and enter the context — manager.RegisterAsync
        // requires an ambient TenantContext to stamp the scheme with its realm.
        // Mirrors SamlLoginProviderReRegister.Run.
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
