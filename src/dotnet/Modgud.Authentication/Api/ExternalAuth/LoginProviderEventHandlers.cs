using Marten;
using Modgud.Authentication.Domain.LoginProviders.Events;

namespace Modgud.Authentication.Api.ExternalAuth;

// Wolverine discovers handler classes by name/convention (`*Handler` with a
// `Handle` method). One class per event keeps discovery explicit and matches
// the pattern used by AutoMembershipSync* in the Groups feature.
//
// ADR 0010 (D6): these handlers no longer register or unregister schemes
// themselves — that would only ever affect the node that committed the change.
// They ask the materializer to re-read the realm's providers now, so this
// node's OIDC schemes and SAML cache are exact the moment the admin's request
// returns; every other node picks the change up from the database within
// LoginProviderSchemeMaterializer.RevalidateInterval. The Marten session is
// tenant-scoped through the message envelope, which is how the realm is known.

public class LoginProviderOnAddedHandler(IQuerySession session, LoginProviderSchemeMaterializer materializer)
{
    public Task Handle(LoginProviderAddedEvent @event, CancellationToken ct) =>
        LoginProviderSchemeRefresh.Run(session, materializer, ct);
}

public class LoginProviderOnUpdatedHandler(IQuerySession session, LoginProviderSchemeMaterializer materializer)
{
    public Task Handle(LoginProviderUpdatedEvent @event, CancellationToken ct) =>
        LoginProviderSchemeRefresh.Run(session, materializer, ct);
}

public class LoginProviderOnEnabledHandler(IQuerySession session, LoginProviderSchemeMaterializer materializer)
{
    public Task Handle(LoginProviderEnabledEvent @event, CancellationToken ct) =>
        LoginProviderSchemeRefresh.Run(session, materializer, ct);
}

public class LoginProviderOnDisabledHandler(IQuerySession session, LoginProviderSchemeMaterializer materializer)
{
    public Task Handle(LoginProviderDisabledEvent @event, CancellationToken ct) =>
        LoginProviderSchemeRefresh.Run(session, materializer, ct);
}

public class LoginProviderOnSecretRotatedHandler(IQuerySession session, LoginProviderSchemeMaterializer materializer)
{
    public Task Handle(LoginProviderSecretRotatedEvent @event, CancellationToken ct) =>
        LoginProviderSchemeRefresh.Run(session, materializer, ct);
}

public class LoginProviderOnDeletedHandler(IQuerySession session, LoginProviderSchemeMaterializer materializer)
{
    public Task Handle(LoginProviderDeletedEvent @event, CancellationToken ct) =>
        LoginProviderSchemeRefresh.Run(session, materializer, ct);
}

internal static class LoginProviderSchemeRefresh
{
    public static async Task Run(
        IQuerySession session,
        LoginProviderSchemeMaterializer materializer,
        CancellationToken ct)
    {
        var realm = session.TenantId;
        if (string.IsNullOrEmpty(realm)) return;
        await materializer.RefreshAsync(realm, ct);
    }
}
