using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Projections;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Scopes;
using Modgud.Infrastructure.Persistence.Marten.Projections.OAuth;

namespace Modgud.Tests.Unit;

/// <summary>
/// The projections handle their Created event through <c>Apply(event, current)</c> rather
/// than <c>Create(event)</c>, so a Created event landing on an EXISTING stream revives a
/// soft-deleted entity (provisioning re-import under a pinned id) by rebuilding the
/// document wholesale. Marten passes no current document on a fresh stream; these helpers
/// give the unit tests that same "no current document" call in one place.
/// </summary>
internal static class ProjectionCreateExtensions
{
    public static App ApplyCreated(this AppProjection p, AppCreatedEvent e) => p.Apply(e, null!);

    public static OAuthApiState ApplyCreated(this OAuthApiStateProjection p, OAuthApiCreated e)
        => p.Apply(e, null!);

    public static OAuthApplicationState ApplyCreated(
        this OAuthApplicationStateProjection p, OAuthApplicationCreated e) => p.Apply(e, null!);

    public static OAuthScopeState ApplyCreated(this OAuthScopeStateProjection p, OAuthScopeCreated e)
        => p.Apply(e, null!);
}
