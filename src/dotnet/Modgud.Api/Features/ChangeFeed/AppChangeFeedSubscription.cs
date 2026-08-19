using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingBlocks.Helper;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Subscriptions;
using Modgud.Authentication.Events;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Domain.Applications;
using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.ChangeFeed;

namespace Modgud.Api.Features.ChangeFeed;

/// <summary>
/// High-water-anchored projection into the short-lived per-App resume queue.
/// It publishes net changes to the public integration model, never raw domain
/// event payloads and never a second permanent copy of the event store.
/// </summary>
public sealed class AppChangeFeedSubscription : SubscriptionBase
{
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppChangeFeedSubscription()
    {
        Name = "AppChangeFeed";
        Options.BatchSize = 250;
        Options.MaximumHopperSize = 5_000;
        // This queue is an integration resume window, not a replay of the
        // permanent event store. Enabling the feed writes a fresh event and
        // seeds a full current-state snapshot from that high-water mark.
        Options.SubscribeFromPresent();
    }

    public override async Task<IChangeListener> ProcessEventsAsync(
        EventRange page,
        ISubscriptionController controller,
        IDocumentOperations operations,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var states = await operations.Query<AppChangeFeedState>().ToListAsync(cancellationToken);
        var stateByApp = states.ToDictionary(x => x.Id);
        var configured = await operations.Query<ApplicationSettings>()
            .Where(x => x.ChangeFeed != null)
            .ToListAsync(cancellationToken);
        var settingsByApp = configured.ToDictionary(x => x.Id);

        var configuredInPage = page.Events
            .Select(x => x.Data)
            .OfType<ApplicationChangeFeedConfiguredEvent>()
            .Select(x => x.ApplicationId);
        var appIds = states.Where(x => x.Enabled).Select(x => x.Id)
            .Concat(configured.Where(x => x.ChangeFeed!.Enabled).Select(x => x.Id))
            .Concat(configuredInPage)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var relevantSources = page.Events.Where(IsPublicStateSource).Take(2).ToList();
        var publicStateMayHaveChanged = relevantSources.Count > 0;
        // A subscription page can coalesce several domain events into one net
        // public change. Only expose an origin id when attribution is exact.
        var relevantSource = relevantSources.Count == 1 ? relevantSources[0] : null;

        foreach (var appId in appIds)
        {
            settingsByApp.TryGetValue(appId, out var settingsDocument);
            var policy = settingsDocument?.ChangeFeed ?? ApplicationChangeFeedSettings.Disabled;
            stateByApp.TryGetValue(appId, out var state);

            if (!policy.Enabled)
            {
                if (state is { Enabled: true })
                {
                    state.Enabled = false;
                    state.LastProcessedSequence = page.SequenceCeiling;
                    StageEntry(
                        operations,
                        state,
                        page.SequenceCeiling,
                        ordinal: 0,
                        source: relevantSource,
                        now,
                        changeKind: AppChangeKinds.FeedDisabled,
                        entity: null,
                        reason: "FeedDisabled");
                    operations.Store(state);
                }
                continue;
            }

            var app = await operations.LoadAsync<App>(appId, cancellationToken);
            if (app is null || app.IsDeleted)
            {
                if (state is { Enabled: true })
                {
                    state.Enabled = false;
                    state.LastProcessedSequence = page.SequenceCeiling;
                    StageEntry(
                        operations,
                        state,
                        page.SequenceCeiling,
                        ordinal: 0,
                        source: relevantSource,
                        now,
                        changeKind: AppChangeKinds.FeedDisabled,
                        entity: null,
                        reason: "ApplicationDeleted");
                    operations.Store(state);
                }
                continue;
            }

            var retentionDays = (int)policy.MinimumRetentionAge.TotalDays;
            if (state is null || !state.Enabled)
            {
                state ??= new AppChangeFeedState { Id = appId };
                state.Enabled = true;
                state.Generation = Math.Max(1, state.Generation + 1);
                state.MinimumRetentionAgeDays = retentionDays;
                state.MinimumEventCount = policy.MinimumEventCount;
                state.LastProcessedSequence = page.SequenceCeiling;

                var snapshot = await BuildSnapshotAsync(operations, app, cancellationToken);
                state.ScopeVersion = snapshot.ScopeVersion;
                await ReplaceEntityStateAsync(operations, appId, snapshot.Entities, cancellationToken);
                StageEntry(
                    operations,
                    state,
                    page.SequenceCeiling,
                    ordinal: 0,
                    source: relevantSource,
                    now,
                    changeKind: AppChangeKinds.ScopeChanged,
                    entity: null,
                    reason: "FeedEnabled");
                operations.Store(state);
                continue;
            }

            state.MinimumRetentionAgeDays = retentionDays;
            state.MinimumEventCount = policy.MinimumEventCount;
            state.LastProcessedSequence = page.SequenceCeiling;

            if (publicStateMayHaveChanged)
            {
                var snapshot = await BuildSnapshotAsync(operations, app, cancellationToken);
                if (!string.Equals(state.ScopeVersion, snapshot.ScopeVersion, StringComparison.Ordinal))
                {
                    state.Generation++;
                    state.ScopeVersion = snapshot.ScopeVersion;
                    state.RetentionFloorSequence = 0;
                    state.RetentionFloorOrdinal = -1;
                    await ReplaceEntityStateAsync(operations, appId, snapshot.Entities, cancellationToken);
                    StageEntry(
                        operations,
                        state,
                        page.SequenceCeiling,
                        ordinal: 0,
                        source: relevantSource,
                        now,
                        changeKind: AppChangeKinds.ScopeChanged,
                        entity: null,
                        reason: "ScopeDefinitionChanged");
                }
                else
                {
                    await StageNetChangesAsync(
                        operations,
                        state,
                        snapshot,
                        page.SequenceCeiling,
                        relevantSource,
                        now,
                        cancellationToken);
                }
            }

            if (state.LastCompactedAt is null || state.LastCompactedAt < now.AddHours(-1))
            {
                await CompactAsync(operations, state, now, cancellationToken);
                state.LastCompactedAt = now;
            }

            operations.Store(state);
        }

        return NullChangeListener.Instance;
    }

    private static bool IsPublicStateSource(IEvent source)
    {
        var data = source.Data;
        var ns = data.GetType().Namespace ?? string.Empty;
        if (data is ApplicationChangeFeedConfiguredEvent) return true;
        if (ns == "Modgud.Authorization.Events") return true;
        if (ns == "Modgud.Domain.Users.Events") return true;
        if (ns == "Modgud.Domain.PositionTerminals") return true;

        return data is UserIdentitySetupEvent
            or UserUserNameChangedEvent
            or UserActivatedEvent
            or UserDeactivatedEvent
            or UserExternalIdentityLinkedEvent
            or UserExternalIdentityUnlinkedEvent;
    }

    private static async Task<PublicAppSnapshot> BuildSnapshotAsync(
        IDocumentOperations operations,
        App app,
        CancellationToken cancellationToken)
    {
        var directory = await operations.Query<Principal>().ToListAsync(cancellationToken);
        var scope = ApplicationScopeResolver.BuildSnapshot(app, directory);
        var scopeIds = scope.Principals.Select(x => x.Id).ToHashSet();
        var rootIds = scope.RootGroups.Select(x => x.Id).ToHashSet();
        var positions = scope.Principals.OfType<PositionPrincipal>().Select(x => x.Id).ToHashSet();

        var current = new Dictionary<EntityKey, PublicEntity>();
        var presence = new Dictionary<EntityKey, EntityPresence>();

        foreach (var principal in directory)
        {
            var key = new EntityKey(AppEntityKinds.Principal, principal.Id);
            presence[key] = new EntityPresence(!principal.IsDeleted, null);
        }

        foreach (var principal in scope.Principals)
        {
            var key = new EntityKey(AppEntityKinds.Principal, principal.Id);
            var group = principal as Group;
            var person = principal as Person;
            var serviceAccount = principal as ServiceAccount;
            var position = principal as PositionPrincipal;
            var payload = new
            {
                Id = ShortGuid.Encode(principal.Id),
                principal.Type,
                principal.DisplayName,
                principal.IsActive,
                IsScopeRoot = rootIds.Contains(principal.Id),
                AccountName = person?.AccountName ?? serviceAccount?.AccountName ?? position?.AccountName,
                person?.Firstname,
                person?.Lastname,
                person?.Acronym,
                person?.Email,
                Name = group?.Name,
                Description = group?.Description,
                Purpose = serviceAccount?.Purpose ?? position?.Purpose,
                MemberIds = group?.MemberIds
                    .Where(scopeIds.Contains)
                    .OrderBy(x => x)
                    .Select(ShortGuid.Encode)
                    .ToArray(),
                HasPermissions = group is null ? (bool?)null : group.RoleIds.Count > 0,
                TerminalPolicy = position is null ? null : new
                {
                    position.TerminalPolicy.Enabled,
                    position.TerminalPolicy.AllowedActivationProofs,
                    position.TerminalPolicy.AllowedDeviceBindings,
                    StaffingSessionLifetimeSeconds = (long)position.TerminalPolicy.StaffingSessionLifetime.TotalSeconds,
                    MaximumStaffingSessionLifetimeSeconds = (long)position.TerminalPolicy.MaximumStaffingSessionLifetime.TotalSeconds,
                },
            };
            current[key] = PublicEntity.Create(key, payload);
        }

        var terminals = await operations.Query<TerminalEnrollment>().ToListAsync(cancellationToken);
        var scopedTerminalIds = new HashSet<Guid>();
        foreach (var terminal in terminals)
        {
            var key = new EntityKey(AppEntityKinds.Terminal, terminal.Id);
            var allowed = terminal.EffectiveAllowedPositionIds.Where(positions.Contains).OrderBy(x => x).ToArray();
            var exists = terminal.Status != TerminalEnrollmentStatus.Revoked;
            presence[key] = new EntityPresence(
                exists,
                exists ? null : Serialize(new { terminal.Status, terminal.RevokedAt }));
            if (!exists || allowed.Length == 0) continue;

            scopedTerminalIds.Add(terminal.Id);
            current[key] = PublicEntity.Create(key, new
            {
                Id = ShortGuid.Encode(terminal.Id),
                AllowedPositionIds = allowed.Select(ShortGuid.Encode).ToArray(),
                terminal.DisplayName,
                terminal.Location,
                terminal.ClientId,
                terminal.WebAuthnRpId,
                terminal.Binding,
                terminal.Status,
                ActiveStaffingSessionId = terminal.ActiveStaffingSessionId is { } active
                    ? ShortGuid.Encode(active)
                    : null,
                terminal.CreatedAt,
                terminal.EnrolledAt,
                terminal.DisabledAt,
            });
        }

        var grants = await operations.Query<PositionGrant>().ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            var key = new EntityKey(AppEntityKinds.PositionGrant, grant.Id);
            var exists = grant.Status != PositionGrantStatus.Revoked;
            presence[key] = new EntityPresence(
                exists,
                exists ? null : Serialize(new { grant.Status, grant.RevokedAt }));
            if (!exists || !positions.Contains(grant.PositionPrincipalId) || !scopeIds.Contains(grant.UserId))
                continue;

            current[key] = PublicEntity.Create(key, new
            {
                Id = ShortGuid.Encode(grant.Id),
                PositionId = ShortGuid.Encode(grant.PositionPrincipalId),
                UserId = ShortGuid.Encode(grant.UserId),
                grant.Status,
                grant.CreatedAt,
            });
        }

        var staffingSessions = await operations.Query<StaffingSession>().ToListAsync(cancellationToken);
        foreach (var staffing in staffingSessions)
        {
            var key = new EntityKey(AppEntityKinds.StaffingSession, staffing.Id);
            var exists = staffing.Status == StaffingSessionStatus.Active;
            presence[key] = new EntityPresence(
                exists,
                exists ? null : Serialize(new { staffing.Status, staffing.EndedAt, staffing.EndReason }));
            if (!exists || !positions.Contains(staffing.PositionPrincipalId)
                        || !scopedTerminalIds.Contains(staffing.TerminalEnrollmentId))
                continue;

            current[key] = PublicEntity.Create(key, new
            {
                Id = ShortGuid.Encode(staffing.Id),
                PositionId = ShortGuid.Encode(staffing.PositionPrincipalId),
                TerminalId = ShortGuid.Encode(staffing.TerminalEnrollmentId),
                ActivatedByUserId = scopeIds.Contains(staffing.ActivatedByUserId)
                    ? ShortGuid.Encode(staffing.ActivatedByUserId)
                    : null,
                MethodId = staffing.GetActivationEvidence().MethodId,
                staffing.Status,
                staffing.StartedAt,
                staffing.AbsoluteExpiresAt,
            });
        }

        return new PublicAppSnapshot(scope.ScopeVersion, current, presence);
    }

    private static async Task StageNetChangesAsync(
        IDocumentOperations operations,
        AppChangeFeedState feed,
        PublicAppSnapshot snapshot,
        long sourceSequence,
        IEvent? source,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var persisted = await operations.Query<AppChangeFeedEntityState>()
            .Where(x => x.AppId == feed.Id)
            .ToListAsync(cancellationToken);
        var oldByKey = persisted.ToDictionary(x => new EntityKey(x.EntityKind, x.EntityId));
        var changes = new List<PendingChange>();

        foreach (var (key, entity) in snapshot.Entities)
        {
            if (!oldByKey.TryGetValue(key, out var old))
            {
                changes.Add(new PendingChange(AppChangeKinds.Upsert, entity, null));
                operations.Store(new AppChangeFeedEntityState
                {
                    Id = EntityStateId(feed.Id, key),
                    AppId = feed.Id,
                    EntityKind = key.Kind,
                    EntityId = key.Id,
                    Fingerprint = entity.Fingerprint,
                    PayloadJson = entity.PayloadJson!,
                });
            }
            else if (old.Fingerprint != entity.Fingerprint)
            {
                changes.Add(new PendingChange(AppChangeKinds.Upsert, entity, null));
                old.Fingerprint = entity.Fingerprint;
                old.PayloadJson = entity.PayloadJson!;
                operations.Store(old);
            }
            oldByKey.Remove(key);
        }

        foreach (var (key, old) in oldByKey)
        {
            snapshot.Presence.TryGetValue(key, out var presence);
            var kind = presence.Exists ? AppChangeKinds.FellOutOfScope : AppChangeKinds.Deleted;
            var tombstone = presence.RemovalPayloadJson is null
                ? null
                : new PublicEntity(key, presence.RemovalPayloadJson, Fingerprint(presence.RemovalPayloadJson));
            changes.Add(new PendingChange(kind, tombstone ?? new PublicEntity(key, null, string.Empty), null));
            operations.Delete<AppChangeFeedEntityState>(old.Id);
        }

        var ordinal = 0;
        foreach (var change in changes
                     .OrderBy(x => x.Entity.Key.Kind, StringComparer.Ordinal)
                     .ThenBy(x => x.Entity.Key.Id))
        {
            StageEntry(
                operations,
                feed,
                sourceSequence,
                ordinal++,
                source,
                now,
                change.ChangeKind,
                change.Entity,
                change.Reason);
        }
    }

    private static async Task ReplaceEntityStateAsync(
        IDocumentOperations operations,
        Guid appId,
        IReadOnlyDictionary<EntityKey, PublicEntity> entities,
        CancellationToken cancellationToken)
    {
        var old = await operations.Query<AppChangeFeedEntityState>()
            .Where(x => x.AppId == appId)
            .ToListAsync(cancellationToken);
        var oldById = old.ToDictionary(x => x.Id);
        foreach (var entity in entities.Values)
        {
            var id = EntityStateId(appId, entity.Key);
            if (oldById.Remove(id, out var existing))
            {
                existing.EntityKind = entity.Key.Kind;
                existing.EntityId = entity.Key.Id;
                existing.Fingerprint = entity.Fingerprint;
                existing.PayloadJson = entity.PayloadJson!;
                operations.Store(existing);
            }
            else
            {
                operations.Store(new AppChangeFeedEntityState
                {
                    Id = id,
                    AppId = appId,
                    EntityKind = entity.Key.Kind,
                    EntityId = entity.Key.Id,
                    Fingerprint = entity.Fingerprint,
                    PayloadJson = entity.PayloadJson!,
                });
            }
        }
        foreach (var row in oldById.Values)
            operations.Delete<AppChangeFeedEntityState>(row.Id);
    }

    private static void StageEntry(
        IDocumentOperations operations,
        AppChangeFeedState state,
        long sourceSequence,
        int ordinal,
        IEvent? source,
        DateTimeOffset now,
        string changeKind,
        PublicEntity? entity,
        string? reason)
    {
        operations.Store(new AppChangeFeedEntry
        {
            Id = EntryId(state.Id, state.Generation, sourceSequence, ordinal),
            AppId = state.Id,
            Generation = state.Generation,
            SourceSequence = sourceSequence,
            Ordinal = ordinal,
            ScopeVersion = state.ScopeVersion,
            SourceEventId = source?.Id,
            OriginatedAt = source?.Timestamp ?? now,
            RecordedAt = now,
            ChangeKind = changeKind,
            EntityKind = entity?.Key.Kind,
            EntityId = entity?.Key.Id,
            PayloadJson = entity?.PayloadJson,
            Reason = reason,
        });
    }

    private static async Task CompactAsync(
        IDocumentOperations operations,
        AppChangeFeedState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var countFloor = await operations.Query<AppChangeFeedEntry>()
            .Where(x => x.AppId == state.Id)
            .OrderByDescending(x => x.SourceSequence)
            .ThenByDescending(x => x.Ordinal)
            .Skip(state.MinimumEventCount - 1)
            .FirstOrDefaultAsync(cancellationToken);
        if (countFloor is null) return;

        var ageFloor = now.AddDays(-state.MinimumRetentionAgeDays);
        var removable = await operations.Query<AppChangeFeedEntry>()
            .Where(x => x.AppId == state.Id
                        && x.RecordedAt < ageFloor
                        && (x.SourceSequence < countFloor.SourceSequence
                            || (x.SourceSequence == countFloor.SourceSequence
                                && x.Ordinal < countFloor.Ordinal)))
            .ToListAsync(cancellationToken);

        foreach (var entry in removable)
        {
            operations.Delete<AppChangeFeedEntry>(entry.Id);
            if (entry.SourceSequence > state.RetentionFloorSequence
                || (entry.SourceSequence == state.RetentionFloorSequence
                    && entry.Ordinal > state.RetentionFloorOrdinal))
            {
                state.RetentionFloorSequence = entry.SourceSequence;
                state.RetentionFloorOrdinal = entry.Ordinal;
            }
        }
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, PayloadJson);

    private static string Fingerprint(string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static Guid EntityStateId(Guid appId, EntityKey key) =>
        DeterministicGuid($"state|{appId:N}|{key.Kind}|{key.Id:N}");

    private static Guid EntryId(Guid appId, int generation, long sequence, int ordinal) =>
        DeterministicGuid($"entry|{appId:N}|{generation}|{sequence}|{ordinal}");

    private static Guid DeterministicGuid(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(digest.AsSpan(0, 16));
    }

    private readonly record struct EntityKey(string Kind, Guid Id);

    private sealed record PublicEntity(EntityKey Key, string? PayloadJson, string Fingerprint)
    {
        public static PublicEntity Create<T>(EntityKey key, T payload)
        {
            var json = Serialize(payload);
            return new PublicEntity(key, json, AppChangeFeedSubscription.Fingerprint(json));
        }
    }

    private readonly record struct EntityPresence(bool Exists, string? RemovalPayloadJson);
    private sealed record PublicAppSnapshot(
        string ScopeVersion,
        IReadOnlyDictionary<EntityKey, PublicEntity> Entities,
        IReadOnlyDictionary<EntityKey, EntityPresence> Presence);
    private sealed record PendingChange(string ChangeKind, PublicEntity Entity, string? Reason);
}

internal static class AppEntityKinds
{
    public const string Principal = "principal";
    public const string Terminal = "terminal";
    public const string PositionGrant = "position-grant";
    public const string StaffingSession = "staffing-session";
}

internal static class AppChangeKinds
{
    public const string Upsert = "Upsert";
    public const string Deleted = "Deleted";
    public const string FellOutOfScope = "FellOutOfScope";
    public const string ScopeChanged = "ScopeChanged";
    public const string FeedDisabled = "FeedDisabled";
}
