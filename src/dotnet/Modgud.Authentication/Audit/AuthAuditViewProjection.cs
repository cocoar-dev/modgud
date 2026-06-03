using JasperFx.Events;
using Marten.Events.Projections;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authentication.Events;
using Modgud.Domain.Users.Events;

namespace Modgud.Authentication.Audit;

/// <summary>
/// Async <see cref="EventProjection"/> that folds the user- and config-aggregate
/// streams into the flat <see cref="AuthAuditView"/> read model — one row per
/// audited event. This is deliberately an <c>EventProjection</c>, not a
/// Single/MultiStream aggregation: an audit trail is a <i>list of occurrences</i>,
/// not a per-aggregate snapshot. (See <c>dev-docs/future-features/logging-audit-redesign.md</c> §A.3.)
///
/// <para>Metadata comes from the <see cref="IEvent{T}"/> envelope: <c>Id</c> keys
/// the row, <c>Timestamp</c> is the occurrence time, <c>TenantId</c> is the realm
/// slug, and for user-stream events <c>StreamId</c> is the subject user id. No PII
/// payload is copied into the view — see <see cref="AuthAuditView"/>.</para>
///
/// <para><c>partial</c> because Marten 9's source generator emits the event
/// dispatcher into the class — see <c>dev-docs/engineering-gotchas/marten-raise-side-effects.md</c>.</para>
///
/// <para>SCOPE (Phase 0): user-aggregate auth/lifecycle events + the login-provider
/// config family. OAuth application/scope/API config events are the next mechanical
/// addition (same pattern: one <c>Create(IEvent&lt;T&gt;)</c> per type).</para>
/// </summary>
public partial class AuthAuditViewProjection : EventProjection
{
    private static AuthAuditView Row(
        IEvent e,
        string category,
        string eventType,
        Guid? userId = null,
        Guid? targetId = null,
        string? ip = null,
        string level = "Info") =>
        new()
        {
            Id = e.Id,
            Timestamp = e.Timestamp,
            Realm = e.TenantId,
            Category = category,
            EventType = eventType,
            UserId = userId,
            TargetId = targetId,
            Ip = ip,
            Level = level,
        };

    // ── Authentication (user stream — StreamId == userId) ────────────
    public AuthAuditView Create(IEvent<UserLoggedInEvent> e) =>
        Row(e, AuditCategories.Authentication, AuditEvents.LoginSucceeded, userId: e.StreamId, ip: e.Data.IpAddress);

    public AuthAuditView Create(IEvent<UserLoginFailedEvent> e) =>
        Row(e, AuditCategories.Authentication, AuditEvents.LoginFailed, userId: e.StreamId, ip: e.Data.IpAddress, level: "Warning");

    public AuthAuditView Create(IEvent<UserLockedOutEvent> e) =>
        Row(e, AuditCategories.Authentication, AuditEvents.AccountLockedOut, userId: e.StreamId, level: "Warning");

    public AuthAuditView Create(IEvent<UserUnlockedEvent> e) =>
        Row(e, AuditCategories.Authentication, AuditEvents.AccountUnlocked, userId: e.StreamId);

    // ── Account lifecycle (user stream) ──────────────────────────────
    public AuthAuditView Create(IEvent<UserCreatedEvent> e) =>
        Row(e, AuditCategories.Account, AuditEvents.AccountCreated, userId: e.StreamId);

    public AuthAuditView Create(IEvent<UserDeletedEvent> e) =>
        Row(e, AuditCategories.Account, AuditEvents.AccountDeleted, userId: e.StreamId, level: "Warning");

    public AuthAuditView Create(IEvent<UserUpdatedEvent> e) =>
        Row(e, AuditCategories.Account, AuditEvents.AccountProfileUpdated, userId: e.StreamId);

    public AuthAuditView Create(IEvent<UserUserNameChangedEvent> e) =>
        Row(e, AuditCategories.Account, AuditEvents.AccountUserNameChanged, userId: e.StreamId);

    public AuthAuditView Create(IEvent<UserPasswordChangedEvent> e) =>
        Row(e, AuditCategories.Account, AuditEvents.AccountPasswordChanged, userId: e.StreamId);

    public AuthAuditView Create(IEvent<UserActivatedEvent> e) =>
        Row(e, AuditCategories.Account, AuditEvents.AccountActivated, userId: e.StreamId);

    public AuthAuditView Create(IEvent<UserDeactivatedEvent> e) =>
        Row(e, AuditCategories.Account, AuditEvents.AccountDeactivated, userId: e.StreamId, level: "Warning");

    // ── Federation (user-stream mirror events) ───────────────────────
    public AuthAuditView Create(IEvent<UserExternalIdentityLinkedEvent> e) =>
        Row(e, AuditCategories.Federation, AuditEvents.IdentityLinked, userId: e.StreamId);

    public AuthAuditView Create(IEvent<UserExternalIdentityUnlinkedEvent> e) =>
        Row(e, AuditCategories.Federation, AuditEvents.IdentityUnlinked, userId: e.StreamId);

    // ── Admin / realm config (login-provider stream — StreamId == provider id) ──
    public AuthAuditView Create(IEvent<LoginProviderAddedEvent> e) =>
        Row(e, AuditCategories.AdminRealm, AuditEvents.LoginProviderAdded, targetId: e.StreamId);

    public AuthAuditView Create(IEvent<LoginProviderUpdatedEvent> e) =>
        Row(e, AuditCategories.AdminRealm, AuditEvents.LoginProviderUpdated, targetId: e.StreamId);

    public AuthAuditView Create(IEvent<LoginProviderEnabledEvent> e) =>
        Row(e, AuditCategories.AdminRealm, AuditEvents.LoginProviderEnabled, targetId: e.StreamId);

    public AuthAuditView Create(IEvent<LoginProviderDisabledEvent> e) =>
        Row(e, AuditCategories.AdminRealm, AuditEvents.LoginProviderDisabled, targetId: e.StreamId);

    public AuthAuditView Create(IEvent<LoginProviderSecretRotatedEvent> e) =>
        Row(e, AuditCategories.AdminRealm, AuditEvents.LoginProviderSecretRotated, targetId: e.StreamId, level: "Warning");

    public AuthAuditView Create(IEvent<LoginProviderDeletedEvent> e) =>
        Row(e, AuditCategories.AdminRealm, AuditEvents.LoginProviderDeleted, targetId: e.StreamId, level: "Warning");
}
